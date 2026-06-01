using System.IO.Compression;
using Dmart.Config;
using Dmart.Models.Api;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Security: the zip import path must reject decompression bombs BEFORE
// processing — an archive whose central directory declares an absurd entry
// count (or total uncompressed size) is refused up front, so a 50 MB upload
// can't be expanded into many GB of parsing.
public class ImportLimitsTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ImportLimitsTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task ImportZip_Exceeding_Entry_Cap_Is_Rejected_Before_Processing()
    {
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<DmartSettings>(s => s.ImportMaxEntries = 2)));
        var io = factory.Services.GetRequiredService<ImportExportService>();

        // Five tiny entries — invalid as dmart metas, but the cap must reject
        // before any of them is parsed, so their content is irrelevant.
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < 5; i++)
            {
                var entry = zip.CreateEntry($"junk/{i}.json");
                using var w = new StreamWriter(entry.Open());
                w.Write("{}");
            }
        }
        ms.Position = 0;

        var resp = await io.ImportZipAsync(ms, actor: null);
        resp.Status.ShouldBe(Status.Failed);
        resp.Error!.Code.ShouldBe(InternalErrorCode.INVALID_DATA);
        resp.Error.Message.ShouldContain("too many entries");
    }
}
