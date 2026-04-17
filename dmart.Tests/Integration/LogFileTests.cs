using System.Net.Http.Json;
using System.Text.Json;
using Dmart;
using Dmart.Config;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Confirms LOG_FILE behaviour (Python parity):
//   * file content is strictly JSON Lines
//   * every API call produces a "Served request" record
//   * 4xx/5xx get level=WARNING/ERROR, 2xx gets level=INFO
//   * secrets in request bodies and headers are redacted as ******
public sealed class LogFileTests
{
    [Fact]
    public void LogSink_Inactive_When_LogFile_Empty()
    {
        var s = Options.Create(new DmartSettings { LogFile = "" });
        using var sink = new LogSink(s);
        sink.IsActive.ShouldBeFalse();
        // Should no-op safely.
        sink.WriteLog("x", LogLevel.Information, "y");
        sink.WriteAccessRecord(new() { ["a"] = 1 });
    }

    [Fact]
    public void LogSink_Writes_Valid_JsonLines()
    {
        var path = NewTempLog();
        try
        {
            var s = Options.Create(new DmartSettings { LogFile = path });
            using (var sink = new LogSink(s))
            {
                sink.IsActive.ShouldBeTrue();
                sink.WriteLog("Dmart.Test", LogLevel.Information, "plain event");
                sink.WriteAccessRecord(new Dictionary<string, object?>
                {
                    ["message"] = "Served request",
                    ["props"] = new Dictionary<string, object?>
                    {
                        ["path"] = "/test",
                        ["http_status"] = 200,
                        ["nested"] = new List<object?> { "a", 1, true, null },
                    },
                });
            }

            var lines = File.ReadAllLines(path);
            lines.Length.ShouldBe(2);
            foreach (var line in lines)
            {
                // Every line must be a standalone JSON object.
                using var doc = JsonDocument.Parse(line);
                doc.RootElement.ValueKind.ShouldBe(JsonValueKind.Object);
            }

            using var access = JsonDocument.Parse(lines[1]);
            access.RootElement.GetProperty("message").GetString().ShouldBe("Served request");
            access.RootElement.GetProperty("props").GetProperty("http_status").GetInt32().ShouldBe(200);
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task Login_With_LogFile_Logs_AccessRecord_AndRedactsPassword()
    {
        if (!DmartFactory.HasPg) return;

        var path = NewTempLog();
        try
        {
            // Resolve the bootstrap admin password the same way DmartFactory
            // does, so this test works whether the runner has DMART_TEST_PWD
            // set, a config.env ADMIN_PASSWORD, or neither.
            var adminPassword = ResolveAdminPassword();
            using var factory = new LogFileFactory(path, adminPassword);
            using var client = factory.CreateClient();

            // Successful login — body has password that MUST be redacted.
            var okResp = await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest("dmart", null, null, adminPassword, null),
                DmartJsonContext.Default.UserLoginRequest);
            okResp.IsSuccessStatusCode.ShouldBeTrue();

            // Failed login — also must be logged.
            var badResp = await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest("dmart", null, null, "definitely-wrong", null),
                DmartJsonContext.Default.UserLoginRequest);
            badResp.IsSuccessStatusCode.ShouldBeFalse();

            // Reset attempt count so other tests don't inherit a locked admin.
            var users = factory.Services.GetRequiredService<Dmart.DataAdapters.Sql.UserRepository>();
            await users.ResetAttemptsAsync("dmart");

            // Allow any trailing writes to flush — FileStream autoflush is
            // synchronous but we want the process-level file handle released.
            await Task.Delay(100);

            var loginLines = File.ReadAllLines(path)
                .Where(l => l.Contains("\"path\":\"/user/login\"", StringComparison.Ordinal))
                .Select(l => JsonDocument.Parse(l))
                .ToList();
            loginLines.Count.ShouldBeGreaterThanOrEqualTo(2);

            JsonElement? okLine = null, badLine = null;
            foreach (var doc in loginLines)
            {
                var props = doc.RootElement.GetProperty("props");
                var status = props.GetProperty("response").GetProperty("http_status").GetInt32();
                if (status == 200) okLine = props;
                else if (status == 401) badLine = props;
            }
            okLine.ShouldNotBeNull();
            badLine.ShouldNotBeNull();

            // Level inference: 2xx → INFO, 4xx → WARNING.
            foreach (var doc in loginLines)
            {
                var status = doc.RootElement.GetProperty("props").GetProperty("response").GetProperty("http_status").GetInt32();
                var level = doc.RootElement.GetProperty("level").GetString();
                if (status < 400) level.ShouldBe("INFO");
                else if (status < 500) level.ShouldBe("WARNING");
                else level.ShouldBe("ERROR");
            }

            // Redaction: password never appears in the log in cleartext.
            var okPw = okLine!.Value.GetProperty("request").GetProperty("body").GetProperty("password").GetString();
            okPw.ShouldBe("******");
            var badPw = badLine!.Value.GetProperty("request").GetProperty("body").GetProperty("password").GetString();
            badPw.ShouldBe("******");

            // Redaction: response body access_token also masked.
            var okResponseBody = okLine!.Value.GetProperty("response").GetProperty("body");
            if (okResponseBody.TryGetProperty("records", out var records) && records.GetArrayLength() > 0)
            {
                var attrs = records[0].GetProperty("attributes");
                if (attrs.TryGetProperty("access_token", out var at))
                    at.GetString().ShouldBe("******");
            }
        }
        finally { Delete(path); }
    }

    // ---- helpers ----

    private static string NewTempLog() =>
        Path.Combine(Path.GetTempPath(), $"dmart-logtest-{Guid.NewGuid():N}.ljson.log");

    private static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    // Matches DmartFactory's lookup so the test uses whichever password the
    // bootstrap admin was actually created with on this runner (varies
    // between dev laptop, CI runner, and local config.env).
    private static string ResolveAdminPassword()
    {
        var fromEnv = Environment.GetEnvironmentVariable("DMART_TEST_PWD");
        if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;
        var configFile = DotEnv.FindConfigFile();
        if (configFile is not null)
        {
            var raw = DotEnv.Parse(configFile);
            if (raw.TryGetValue("ADMIN_PASSWORD", out var pw) && !string.IsNullOrEmpty(pw))
                return pw;
        }
        return "testpassword12345";
    }

    // Dedicated factory that mirrors DmartFactory's config overrides and
    // adds a LOG_FILE override. Each test creates its own so the file
    // handle lifetime is scoped to the test and isn't shared with the
    // default DmartFactory used by the rest of the suite.
    private sealed class LogFileFactory : WebApplicationFactory<Program>
    {
        private readonly string _logFilePath;
        private readonly string _adminPassword;
        public LogFileFactory(string logFilePath, string adminPassword)
        {
            _logFilePath = logFilePath;
            _adminPassword = adminPassword;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            Environment.SetEnvironmentVariable("BACKEND_ENV", "/dev/null");
            builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Error));
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var overrides = new Dictionary<string, string?>
                {
                    ["Dmart:JwtSecret"] = "test-secret-test-secret-test-secret-32-bytes",
                    ["Dmart:JwtIssuer"] = "dmart",
                    ["Dmart:JwtAudience"] = "dmart",
                    ["Dmart:JwtAccessMinutes"] = "5",
                    ["Dmart:AdminPassword"] = _adminPassword,
                    ["Dmart:AdminEmail"] = "admin@test.local",
                    ["Dmart:LogFile"] = _logFilePath,
                };
                if (!string.IsNullOrEmpty(DmartFactory.PgConn))
                {
                    overrides["Dmart:PostgresConnection"] = DmartFactory.PgConn;
                    overrides["Dmart:DatabaseHost"] = null;
                    overrides["Dmart:DatabasePassword"] = null;
                    overrides["Dmart:DatabaseName"] = null;
                }
                cfg.AddInMemoryCollection(overrides);
            });
        }
    }
}
