using System.Text.Json;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Round-trip tests for source-gen JSON. The repositories store/load entries via
// JsonSerializer + DmartJsonContext, so this verifies that contract.
public class EntryMaterializationTests
{
    [Fact]
    public void Entry_Serializes_With_Snake_Case_Property_Names()
    {
        var e = new Entry
        {
            Uuid = "550e8400-e29b-41d4-a716-446655440000",
            Shortname = "hello",
            SpaceName = "demo",
            Subpath = "/notes",
            ResourceType = ResourceType.Content,
            OwnerShortname = "admin",
            Displayname = new Translation(En: "Hello world"),
            Tags = new() { "intro", "test" },
        };
        var json = JsonSerializer.Serialize(e, DmartJsonContext.Default.Entry);
        json.ShouldContain("\"space_name\":\"demo\"");
        json.ShouldContain("\"shortname\":\"hello\"");
        json.ShouldContain("\"resource_type\":\"content\"");      // dmart enum string
        json.ShouldContain("\"displayname\":{\"en\":\"Hello world\"}");
        json.ShouldContain("\"owner_shortname\":\"admin\"");
    }

    [Fact]
    public void Entry_Round_Trips_Through_Source_Gen_Json()
    {
        var original = new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = "ticket-1",
            SpaceName = "support",
            Subpath = "/tickets",
            ResourceType = ResourceType.Ticket,
            OwnerShortname = "alice",
            State = "open",
            IsOpen = true,
            WorkflowShortname = "default",
        };
        var json = JsonSerializer.Serialize(original, DmartJsonContext.Default.Entry);
        var restored = JsonSerializer.Deserialize(json, DmartJsonContext.Default.Entry);
        restored.ShouldNotBeNull();
        restored!.Shortname.ShouldBe(original.Shortname);
        restored.SpaceName.ShouldBe(original.SpaceName);
        restored.ResourceType.ShouldBe(ResourceType.Ticket);
        restored.State.ShouldBe("open");
        restored.IsOpen.ShouldBe(true);
        restored.OwnerShortname.ShouldBe("alice");
    }

    [Fact]
    public void User_Round_Trips_With_Language_Enum()
    {
        var u = new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = "alice",
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = "alice",
            Email = "a@b.c",
            Roles = new() { "super_admin" },
            Language = Language.Ar,
        };
        var json = JsonSerializer.Serialize(u, DmartJsonContext.Default.User);
        // dmart's Language enum stores the FULL spelling, not the ISO code.
        json.ShouldContain("\"language\":\"arabic\"");

        var back = JsonSerializer.Deserialize(json, DmartJsonContext.Default.User);
        back.ShouldNotBeNull();
        back!.Shortname.ShouldBe("alice");
        back.Email.ShouldBe("a@b.c");
        back.Language.ShouldBe(Language.Ar);
        back.Roles.ShouldContain("super_admin");
    }

    [Fact]
    public void Permission_With_Subpaths_Dictionary_Round_Trips()
    {
        var p = new Permission
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = "editors",
            SpaceName = "management",
            Subpath = "permissions",
            OwnerShortname = "admin",
            Subpaths = new() { ["demo"] = new() { "/content/*", "/notes" } },
            ResourceTypes = new() { "content", "folder" },
            Actions = new() { "view", "create", "update" },
        };
        var json = JsonSerializer.Serialize(p, DmartJsonContext.Default.Permission);
        var back = JsonSerializer.Deserialize(json, DmartJsonContext.Default.Permission);
        back.ShouldNotBeNull();
        back!.Subpaths["demo"].ShouldContain("/content/*");
        back.Actions.ShouldContain("view");
    }

    [Fact]
    public void Translation_Records_Locale_Keys()
    {
        var t = new Translation(En: "Hello", Ar: "مرحبا");
        var json = JsonSerializer.Serialize(t, DmartJsonContext.Default.Translation);
        json.ShouldContain("\"en\":\"Hello\"");
        // System.Text.Json escapes non-ASCII to \u sequences by default; the round-trip
        // is what matters for fidelity (not the literal byte form on the wire).
        var back = JsonSerializer.Deserialize(json, DmartJsonContext.Default.Translation);
        back!.En.ShouldBe("Hello");
        back.Ar.ShouldBe("مرحبا");
        back.Ku.ShouldBeNull();
    }

    [Fact]
    public void ResourceType_Enum_Serializes_With_Dmart_String()
    {
        var json = JsonSerializer.Serialize(ResourceType.PluginWrapper, DmartJsonContext.Default.ResourceType);
        json.ShouldBe("\"plugin_wrapper\"");

        var json2 = JsonSerializer.Serialize(ResourceType.DataAsset, DmartJsonContext.Default.ResourceType);
        json2.ShouldBe("\"data_asset\"");
    }
}
