using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// The auto-shortname attachment create relies on TryInsertAsync to REJECT a
// colliding (shortname, space, subpath) — so the handler can re-mint — instead of
// silently overwriting, while UpsertAsync keeps its overwrite-in-place semantics
// for explicit-shortname re-writes. This pins both behaviours.
public class AttachmentTryInsertTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public AttachmentTryInsertTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task TryInsert_Rejects_A_Duplicate_Without_Overwriting_But_Upsert_Still_Overwrites()
    {
        var attachments = _factory.Services.GetRequiredService<AttachmentRepository>();
        var sn = "att_" + Guid.NewGuid().ToString("N")[..8];
        const string space = "management";
        const string subpath = "/attachtest";
        Attachment Make(string body) => new()
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = sn, SpaceName = space, Subpath = subpath,
            ResourceType = ResourceType.Comment,
            OwnerShortname = "dmart", IsActive = true,
            Tags = new(), Body = body,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        try
        {
            // First insert wins.
            (await attachments.TryInsertAsync(Make("original"))).ShouldBeTrue();
            (await attachments.GetAsync(space, subpath, sn))!.Body.ShouldBe("original");

            // A second insert on the same (shortname, space, subpath) is rejected —
            // and the existing row is NOT clobbered (this is what lets the auto path
            // re-mint instead of destroying data).
            (await attachments.TryInsertAsync(Make("clobber"))).ShouldBeFalse();
            (await attachments.GetAsync(space, subpath, sn))!.Body.ShouldBe("original");

            // UpsertAsync keeps overwrite-in-place for explicit-shortname re-writes.
            await attachments.UpsertAsync(Make("updated"));
            (await attachments.GetAsync(space, subpath, sn))!.Body.ShouldBe("updated");
        }
        finally
        {
            var existing = await attachments.GetAsync(space, subpath, sn);
            if (existing is not null) await attachments.DeleteAsync(Guid.Parse(existing.Uuid));
        }
    }
}
