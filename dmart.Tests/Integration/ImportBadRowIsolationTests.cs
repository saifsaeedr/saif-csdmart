using System.IO.Compression;
using System.Text;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// A default (non --fast) import must isolate bad rows instead of failing the
// run: a row whose owner_shortname has no users row (FK violation at commit —
// the owner FKs are DEFERRABLE INITIALLY DEFERRED) and a row whose uuid
// collides with another row's PK must each fail ALONE, while every good row
// in the same import still lands. This pins the per-row semantics the
// importer has always had on the default path, so the batched COPY + merge
// default (with its row-by-row integrity fallback) can't regress them.
public class ImportBadRowIsolationTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ImportBadRowIsolationTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Default_Import_Isolates_Bad_Rows_And_Lands_Good_Ones()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var io = sp.GetRequiredService<ImportExportService>();
        var entryRepo = sp.GetRequiredService<EntryRepository>();
        var db = sp.GetRequiredService<Db>();

        var spaceName = "impiso_" + Guid.NewGuid().ToString("N")[..6];
        var dupUuid = Guid.NewGuid().ToString();

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddJson(zip, $"{spaceName}/.dm/meta.space.json",
                $$"""{"uuid":"{{Guid.NewGuid()}}","shortname":"{{spaceName}}","is_active":true,"owner_shortname":"dmart"}""");
            AddEntryMeta(zip, spaceName, "good1", owner: "dmart", uuid: Guid.NewGuid().ToString());
            // Unknown owner → FK violation (entries_owner_shortname_fkey).
            AddEntryMeta(zip, spaceName, "bad_owner", owner: "no_such_user_x421", uuid: Guid.NewGuid().ToString());
            // Two rows sharing one uuid → PK violation on the second.
            AddEntryMeta(zip, spaceName, "dup_a", owner: "dmart", uuid: dupUuid);
            AddEntryMeta(zip, spaceName, "dup_b", owner: "dmart", uuid: dupUuid);
            AddEntryMeta(zip, spaceName, "good2", owner: "dmart", uuid: Guid.NewGuid().ToString());
        }
        ms.Position = 0;

        try
        {
            var resp = await io.ImportZipAsync(ms, actor: null);

            resp.Status.ShouldBe(Status.Success);
            (await entryRepo.GetAsync(spaceName, "/stuff", "good1", ResourceType.Content)).ShouldNotBeNull();
            (await entryRepo.GetAsync(spaceName, "/stuff", "good2", ResourceType.Content)).ShouldNotBeNull();
            (await entryRepo.GetAsync(spaceName, "/stuff", "bad_owner", ResourceType.Content))
                .ShouldBeNull("a row with an unknown owner must fail its FK check, not import");

            var dupA = await entryRepo.GetAsync(spaceName, "/stuff", "dup_a", ResourceType.Content);
            var dupB = await entryRepo.GetAsync(spaceName, "/stuff", "dup_b", ResourceType.Content);
            ((dupA is null) ^ (dupB is null)).ShouldBeTrue(
                "exactly one of the uuid-duplicate pair should land");

            var attrs = resp.Attributes!;
            ((int)attrs["entries_inserted"]).ShouldBe(3);
            ((int)attrs["failed_count"]).ShouldBeGreaterThanOrEqualTo(2);
        }
        finally
        {
            await using var conn = await db.OpenAsync();
            await using (var del = new NpgsqlCommand(
                "DELETE FROM entries WHERE space_name = $1", conn)
                { Parameters = { new() { Value = spaceName } } })
                await del.ExecuteNonQueryAsync();
            await using (var del = new NpgsqlCommand(
                "DELETE FROM spaces WHERE shortname = $1", conn)
                { Parameters = { new() { Value = spaceName } } })
                await del.ExecuteNonQueryAsync();
        }
    }

    private static void AddEntryMeta(
        ZipArchive zip, string space, string shortname, string owner, string uuid)
        => AddJson(zip, $"{space}/stuff/.dm/{shortname}/meta.content.json",
            $$"""{"uuid":"{{uuid}}","shortname":"{{shortname}}","is_active":true,"owner_shortname":"{{owner}}","resource_type":"content"}""");

    private static void AddJson(ZipArchive zip, string path, string json)
    {
        var entry = zip.CreateEntry(path);
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(json);
    }
}
