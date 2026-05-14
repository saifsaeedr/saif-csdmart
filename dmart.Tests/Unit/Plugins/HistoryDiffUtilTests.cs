using System.Text.Json;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Plugins;

// Pins the two diff producers extracted from EntryService/UserService into
// HistoryDiffUtil. Both the REST path and the native-plugin SaveEntryCb /
// UpdateUserCb route through these — drift here drifts the audit shape.
public sealed class HistoryDiffUtilTests
{
    [Fact]
    public void ComputeEntryDiff_NoChange_Returns_Empty()
    {
        var e1 = BaseEntry();
        var e2 = BaseEntry();
        HistoryDiffUtil.ComputeEntryDiff(e1, e2).ShouldBeEmpty();
    }

    [Fact]
    public void ComputeEntryDiff_State_Flip_Produces_OldNew()
    {
        var e1 = BaseEntry();
        var e2 = BaseEntry() with { State = "confirmed" };
        var diff = HistoryDiffUtil.ComputeEntryDiff(e1, e2);
        diff.ShouldContainKey("state");
        var pair = (Dictionary<string, object?>)diff["state"];
        pair["old"].ShouldBe("new");
        pair["new"].ShouldBe("confirmed");
    }

    [Fact]
    public void ComputeEntryDiff_Translation_Flattens_Per_Locale()
    {
        var e1 = BaseEntry();
        var e2 = BaseEntry() with { Displayname = new Translation(En: "Hello") };
        var diff = HistoryDiffUtil.ComputeEntryDiff(e1, e2);
        diff.ShouldContainKey("displayname.en");
        var pair = (Dictionary<string, object?>)diff["displayname.en"];
        pair["old"].ShouldBeNull();
        pair["new"].ShouldBe("Hello");
    }

    [Fact]
    public void ComputeUserDiff_NoChange_Returns_Empty()
    {
        var u1 = BaseUser();
        var u2 = BaseUser();
        HistoryDiffUtil.ComputeUserDiff(u1, u2).ShouldBeEmpty();
    }

    [Fact]
    public void ComputeUserDiff_Language_Is_WireForm_Not_Enum_Name()
    {
        var u1 = BaseUser();
        var u2 = BaseUser() with { Language = Language.Ar };
        var diff = HistoryDiffUtil.ComputeUserDiff(u1, u2);
        diff.ShouldContainKey("language");
        var pair = (Dictionary<string, object?>)diff["language"];
        // "english"/"arabic" — the wire format, NOT "en"/"ar".
        pair["old"].ShouldBe("english");
        pair["new"].ShouldBe("arabic");
    }

    [Fact]
    public void ComputeUserDiff_Password_AttemptCount_UpdatedAt_Are_Excluded()
    {
        // Even when these secrets/bookkeeping fields change, they must never
        // appear in the diff — the FlattenUser helper deliberately leaves
        // them out so audit consumers never see secrets.
        var u1 = BaseUser();
        var u2 = BaseUser() with
        {
            Password = "different-hash",
            AttemptCount = 99,
            UpdatedAt = DateTime.UtcNow.AddDays(1),
        };
        var diff = HistoryDiffUtil.ComputeUserDiff(u1, u2);
        diff.ShouldNotContainKey("password");
        diff.ShouldNotContainKey("attempt_count");
        diff.ShouldNotContainKey("updated_at");
        diff.ShouldBeEmpty();
    }

    [Fact]
    public void ComputeEntryDiff_Payload_Body_Flattens_Nested_Json()
    {
        var e1 = BaseEntry() with
        {
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonSerializer.SerializeToElement(new { name = "old", score = 1 }),
            },
        };
        var e2 = BaseEntry() with
        {
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonSerializer.SerializeToElement(new { name = "new", score = 1 }),
            },
        };
        var diff = HistoryDiffUtil.ComputeEntryDiff(e1, e2);
        // Only the changed field surfaces.
        diff.ShouldContainKey("payload.body.name");
        diff.ShouldNotContainKey("payload.body.score");
    }

    [Fact]
    public void ComputeUserDiff_Payload_Body_Flattens_Nested_Json()
    {
        // Mirrors the entry-payload test for users: only fields that actually
        // changed in the JSON payload should appear in the diff, with their
        // path joined under `payload.body`. Pins the flatten branch in
        // HistoryDiffUtil.FlattenUser that the existing tests miss.
        var u1 = BaseUser() with
        {
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonSerializer.SerializeToElement(new
                {
                    avatar_url = "https://old.example/a.png",
                    bio = "unchanged",
                }),
            },
        };
        var u2 = BaseUser() with
        {
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonSerializer.SerializeToElement(new
                {
                    avatar_url = "https://new.example/a.png",
                    bio = "unchanged",
                }),
            },
        };
        var diff = HistoryDiffUtil.ComputeUserDiff(u1, u2);
        diff.ShouldContainKey("payload.body.avatar_url");
        diff.ShouldNotContainKey("payload.body.bio");
        var pair = (Dictionary<string, object?>)diff["payload.body.avatar_url"];
        pair["old"].ShouldNotBe(pair["new"]);
    }

    private static Entry BaseEntry() => new()
    {
        Uuid = "00000000-0000-0000-0000-000000000001",
        Shortname = "e1",
        SpaceName = "test",
        Subpath = "/",
        ResourceType = ResourceType.Ticket,
        IsActive = true,
        OwnerShortname = "tester",
        State = "new",
        CreatedAt = DateTime.UnixEpoch,
        UpdatedAt = DateTime.UnixEpoch,
    };

    private static User BaseUser() => new()
    {
        Uuid = "00000000-0000-0000-0000-000000000002",
        Shortname = "u1",
        SpaceName = "management",
        Subpath = "/users",
        OwnerShortname = "u1",
        IsActive = true,
        Email = "u1@test.local",
        Language = Language.En,
        Type = UserType.Web,
        Roles = new(),
        Groups = new(),
        CreatedAt = DateTime.UnixEpoch,
        UpdatedAt = DateTime.UnixEpoch,
    };
}
