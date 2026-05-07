using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Dmart.Config;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Verifies the audit-log writer that produces parity with Python dmart's
// spaces_folder/<space>/.dm/events.jsonl. Asserts: (1) disabled when
// SpacesFolder is empty, (2) the file lands at the right path, (3) each line
// carries Python's nested shape (resource block + request key + ISO-6
// timestamp), (4) per-space isolation.
public class SpaceEventLoggerTests
{
    private static (SpaceEventLogger Logger, string Root) Build(string spacesFolder,
        IHttpContextAccessor? accessor = null)
    {
        var settings = new DmartSettings { SpacesFolder = spacesFolder };
        var logger = new SpaceEventLogger(Options.Create(settings),
            NullLogger<SpaceEventLogger>.Instance, accessor);
        return (logger, spacesFolder);
    }

    // Tiny stub so we can drive a fake request through the logger without
    // standing up an ASP.NET pipeline.
    private sealed class StubAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private static IHttpContextAccessor MakeAccessorWithHeaders(
        Dictionary<string, string> headers)
    {
        var ctx = new DefaultHttpContext();
        foreach (var kv in headers) ctx.Request.Headers[kv.Key] = kv.Value;
        return new StubAccessor { HttpContext = ctx };
    }

    private static Event SampleEvent(string space = "myspace") => new()
    {
        SpaceName = space,
        Subpath = "/users",
        Shortname = "alice",
        ActionType = ActionType.Create,
        ResourceType = ResourceType.User,
        UserShortname = "tester",
        Uuid = "11111111-1111-1111-1111-111111111111",
        Tags = new() { "vip", "beta" },
    };

    [Fact]
    public void Enabled_Is_False_When_SpacesFolder_Empty()
    {
        var (logger, _) = Build("");
        logger.Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task LogAsync_NoOp_When_Disabled()
    {
        // Even with a non-existent path, disabled mode must not throw or
        // create directories — it's a silent skip so prod stays untouched
        // when SpacesFolder isn't configured.
        var (logger, _) = Build("");
        await logger.LogAsync(SampleEvent());
    }

    [Fact]
    public void ResolveLogPath_Produces_DotDm_Layout()
    {
        var (logger, root) = Build("/tmp/spaces-x");
        var p = logger.ResolveLogPath("myspace");
        // Python parity: file is "events.jsonl" (no .log suffix).
        p.ShouldBe(Path.Combine(root, "myspace", ".dm", "events.jsonl"));
    }

    [Fact]
    public async Task LogAsync_Writes_Python_Shape_With_Resource_Block()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "csdmart-evt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (logger, _) = Build(tmp);
            var e = SampleEvent();
            await logger.LogAsync(e);

            var path = Path.Combine(tmp, e.SpaceName, ".dm", "events.jsonl");
            File.Exists(path).ShouldBeTrue();
            var lines = await File.ReadAllLinesAsync(path);
            lines.Length.ShouldBe(1);

            using var doc = JsonDocument.Parse(lines[0]);
            var root = doc.RootElement;

            // Top-level keys (Python's order: resource, user_shortname,
            // request, timestamp, attributes).
            root.GetProperty("user_shortname").GetString().ShouldBe(e.UserShortname);
            root.GetProperty("request").GetString().ShouldBe("create");
            root.GetProperty("timestamp").GetString().ShouldNotBeNullOrWhiteSpace();
            root.GetProperty("attributes").ValueKind.ShouldBe(JsonValueKind.Object);

            // Nested resource block (Locator).
            var resource = root.GetProperty("resource");
            resource.GetProperty("uuid").GetString().ShouldBe(e.Uuid);
            resource.GetProperty("type").GetString().ShouldBe("user");
            resource.GetProperty("space_name").GetString().ShouldBe(e.SpaceName);
            resource.GetProperty("subpath").GetString().ShouldBe(e.Subpath);
            resource.GetProperty("shortname").GetString().ShouldBe(e.Shortname);
            // Tags are a JSON array, preserved verbatim.
            var tags = resource.GetProperty("tags");
            tags.ValueKind.ShouldBe(JsonValueKind.Array);
            tags.GetArrayLength().ShouldBe(2);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void SerializeLine_Timestamp_Is_Six_Digit_Microseconds()
    {
        var line = SpaceEventLogger.SerializeLine(SampleEvent());
        using var doc = JsonDocument.Parse(line);
        var ts = doc.RootElement.GetProperty("timestamp").GetString()!;
        // "yyyy-MM-ddTHH:mm:ss.ffffff" — six digits after the decimal.
        var afterDot = ts.Substring(ts.IndexOf('.') + 1);
        afterDot.Length.ShouldBe(6);
    }

    [Fact]
    public async Task LogAsync_Appends_Multiple_Events_As_Separate_Lines()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "csdmart-evt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (logger, _) = Build(tmp);
            await logger.LogAsync(SampleEvent() with { ActionType = ActionType.Create });
            await logger.LogAsync(SampleEvent() with { ActionType = ActionType.Update });
            await logger.LogAsync(SampleEvent() with { ActionType = ActionType.Delete });

            var path = Path.Combine(tmp, "myspace", ".dm", "events.jsonl");
            var lines = await File.ReadAllLinesAsync(path);
            lines.Length.ShouldBe(3);
            JsonDocument.Parse(lines[0]).RootElement.GetProperty("request").GetString().ShouldBe("create");
            JsonDocument.Parse(lines[1]).RootElement.GetProperty("request").GetString().ShouldBe("update");
            JsonDocument.Parse(lines[2]).RootElement.GetProperty("request").GetString().ShouldBe("delete");
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public async Task LogAsync_Captures_Request_Headers_Into_Attributes()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "csdmart-evt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var accessor = MakeAccessorWithHeaders(new Dictionary<string, string>
            {
                ["User-Agent"] = "csdmart-tests/1.0",
                ["X-Trace-Id"] = "abc-123",
                // These two MUST be stripped — credentials must not land in
                // the audit log.
                ["Cookie"] = "session=secret",
                ["Authorization"] = "Bearer leaked",
            });
            var (logger, _) = Build(tmp, accessor);
            await logger.LogAsync(SampleEvent());

            var path = Path.Combine(tmp, "myspace", ".dm", "events.jsonl");
            var lines = await File.ReadAllLinesAsync(path);
            using var doc = JsonDocument.Parse(lines[0]);
            var headers = doc.RootElement
                .GetProperty("attributes")
                .GetProperty("request_headers");

            headers.GetProperty("User-Agent").GetString().ShouldBe("csdmart-tests/1.0");
            headers.GetProperty("X-Trace-Id").GetString().ShouldBe("abc-123");
            // Excluded — querying the keys must throw KeyNotFoundException.
            Should.Throw<KeyNotFoundException>(() => headers.GetProperty("Cookie"));
            Should.Throw<KeyNotFoundException>(() => headers.GetProperty("Authorization"));
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public async Task LogAsync_Omits_RequestHeaders_When_No_HttpContext()
    {
        // CLI/internal writes have no HttpContext — the writer must NOT emit
        // an empty request_headers object (Python omits the key entirely).
        var tmp = Path.Combine(Path.GetTempPath(), "csdmart-evt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (logger, _) = Build(tmp, accessor: new StubAccessor { HttpContext = null });
            await logger.LogAsync(SampleEvent());

            var path = Path.Combine(tmp, "myspace", ".dm", "events.jsonl");
            using var doc = JsonDocument.Parse((await File.ReadAllLinesAsync(path))[0]);
            var attrs = doc.RootElement.GetProperty("attributes");
            attrs.TryGetProperty("request_headers", out _).ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public async Task LogAsync_Per_Space_Files_Are_Independent()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "csdmart-evt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (logger, _) = Build(tmp);
            await logger.LogAsync(SampleEvent("alpha"));
            await logger.LogAsync(SampleEvent("beta"));

            File.Exists(Path.Combine(tmp, "alpha", ".dm", "events.jsonl")).ShouldBeTrue();
            File.Exists(Path.Combine(tmp, "beta", ".dm", "events.jsonl")).ShouldBeTrue();
            (await File.ReadAllLinesAsync(Path.Combine(tmp, "alpha", ".dm", "events.jsonl")))
                .Length.ShouldBe(1);
            (await File.ReadAllLinesAsync(Path.Combine(tmp, "beta", ".dm", "events.jsonl")))
                .Length.ShouldBe(1);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }
}
