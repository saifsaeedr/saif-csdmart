using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins the wall-clock-only timestamp contract: after the schema change to
// TIMESTAMP WITHOUT TIME ZONE, the value the API emits for `created_at` MUST
// match what psql shows for the same row, and MUST be within seconds of the
// server's local wall clock at write time. Any future regression that
// reintroduces a Local↔Utc shift in the converter or a TIMESTAMPTZ column
// fails one of these tests.
public sealed class TimestampWallClockTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public TimestampWallClockTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Created_At_Matches_DB_Wall_Clock()
    {
        var (client, _, _, cleanup) = await _factory.CreateLoggedInUserAsync();
        var shortname = $"tswc-{Guid.NewGuid():N}".Substring(0, 16);
        var space = "test";
        var subpath = "/itest";

        await CreateContent(client, space, subpath, shortname);

        try
        {
            // Read the raw value straight from Postgres — same DateTime that
            // psql would print, because the column is TIMESTAMP WITHOUT TIME
            // ZONE post-migration. No `AT TIME ZONE` projection.
            var db = _factory.Services.GetRequiredService<Db>();
            await using var conn = await db.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand(
                "SELECT created_at FROM entries WHERE shortname = $1 AND space_name = $2 AND subpath = $3",
                conn);
            cmd.Parameters.Add(new() { Value = shortname });
            cmd.Parameters.Add(new() { Value = space });
            cmd.Parameters.Add(new() { Value = subpath });
            var dbValue = (DateTime)(await cmd.ExecuteScalarAsync())!;

            // Round to seconds — the wire format trims trailing fractional
            // zeros, and psql defaults to microsecond precision; comparing
            // at second precision is enough to assert "no offset shift" and
            // robust against the trailing-zero elision.
            var dbSeconds = new DateTime(dbValue.Year, dbValue.Month, dbValue.Day,
                dbValue.Hour, dbValue.Minute, dbValue.Second);

            var attrs = await GetAttributes(client, space, subpath, shortname);
            attrs.ShouldContainKey("created_at");
            var apiString = ((JsonElement)attrs["created_at"]).GetString()!;
            var apiParsed = DateTime.Parse(apiString, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None);
            var apiSeconds = new DateTime(apiParsed.Year, apiParsed.Month, apiParsed.Day,
                apiParsed.Hour, apiParsed.Minute, apiParsed.Second);

            apiSeconds.ShouldBe(dbSeconds);
            // Also assert the wire format itself never carries an offset.
            apiString.ShouldNotContain("Z");
            apiString.ShouldNotMatch(@"[+-]\d{2}:\d{2}$");
        }
        finally
        {
            await DeleteContent(client, space, subpath, shortname);
            await cleanup();
        }
    }

    [FactIfPg]
    public async Task Created_At_Within_Seconds_Of_Now()
    {
        var (client, _, _, cleanup) = await _factory.CreateLoggedInUserAsync();
        var shortname = $"tsnow-{Guid.NewGuid():N}".Substring(0, 16);
        var space = "test";
        var subpath = "/itest";

        var before = DateTime.Now;
        await CreateContent(client, space, subpath, shortname);
        var after = DateTime.Now;

        try
        {
            var attrs = await GetAttributes(client, space, subpath, shortname);
            var apiString = ((JsonElement)attrs["created_at"]).GetString()!;
            var apiValue = DateTime.Parse(apiString, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None);

            // The server's wall clock at write time must be inside the [before,
            // after] envelope (with a small 5s slack to absorb DB clock drift
            // and CI jitter). A timezone-shifted value would land hours away
            // from this window and the test would fail.
            apiValue.ShouldBeGreaterThanOrEqualTo(before.AddSeconds(-5));
            apiValue.ShouldBeLessThanOrEqualTo(after.AddSeconds(5));
        }
        finally
        {
            await DeleteContent(client, space, subpath, shortname);
            await cleanup();
        }
    }

    // -- helpers --

    private static async Task CreateContent(HttpClient client, string space, string subpath, string shortname)
    {
        var req = new Request
        {
            RequestType = RequestType.Create,
            SpaceName = space,
            Records = new()
            {
                new Record
                {
                    ResourceType = ResourceType.Content,
                    Subpath = subpath,
                    Shortname = shortname,
                    Attributes = new() { ["displayname"] = "wall-clock probe" },
                },
            },
        };
        var resp = await client.PostAsJsonAsync("/managed/request", req, DmartJsonContext.Default.Request);
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Create failed: {resp.StatusCode}\n{body}");
        }
    }

    private static async Task DeleteContent(HttpClient client, string space, string subpath, string shortname)
    {
        try
        {
            var req = new Request
            {
                RequestType = RequestType.Delete,
                SpaceName = space,
                Records = new()
                {
                    new Record
                    {
                        ResourceType = ResourceType.Content,
                        Subpath = subpath,
                        Shortname = shortname,
                    },
                },
            };
            await client.PostAsJsonAsync("/managed/request", req, DmartJsonContext.Default.Request);
        }
        catch { /* best-effort cleanup */ }
    }

    private static async Task<Dictionary<string, object>> GetAttributes(
        HttpClient client, string space, string subpath, string shortname)
    {
        var resp = await client.GetAsync($"/managed/entry/content/{space}/{subpath.TrimStart('/')}/{shortname}");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await resp.Content.ReadAsStringAsync();
        var root = JsonDocument.Parse(json).RootElement;
        var attrs = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var prop in root.EnumerateObject())
            attrs[prop.Name] = prop.Value;
        return attrs;
    }
}
