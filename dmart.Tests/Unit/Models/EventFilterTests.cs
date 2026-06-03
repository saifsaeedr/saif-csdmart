using System.Text.Json;
using Dmart.Models.Core;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Models;

// Pins EventFilter's non-null invariant. The list/dict members are declared
// non-nullable, but System.Text.Json source-gen overwrites a property's default
// with null when a config.json key is present-but-null ("schema_shortnames":
// null). EventFilter coalesces null → empty in its init accessors so that NRE
// class is killed at the type, not at each read site in PluginManager.
public class EventFilterTests
{
    [Fact]
    public void Object_Init_Null_Members_Normalize_To_Empty()
    {
        // The `with`/object-initializer path runs the same init accessors STJ does.
        var f = new EventFilter
        {
            Subpaths = null!,
            ResourceTypes = null!,
            SchemaShortnames = null!,
            Actions = null!,
        };

        f.Subpaths.ShouldBeEmpty();
        f.ResourceTypes.ShouldBeEmpty();
        f.SchemaShortnames.ShouldBeEmpty();
        f.Actions.ShouldBeEmpty();
    }

    [Fact]
    public void Present_But_Null_Json_Keys_Deserialize_To_Empty()
    {
        // The real-world repro: a config.json whose filter keys are explicitly
        // null. Goes through the exact source-gen path used at plugin load.
        const string json =
            "{ \"subpaths\": null, \"resource_types\": null, " +
            "\"schema_shortnames\": null, \"actions\": null }";

        var f = JsonSerializer.Deserialize(json, DmartJsonContext.Default.EventFilter);

        f.ShouldNotBeNull();
        f!.Subpaths.ShouldBeEmpty();
        f.ResourceTypes.ShouldBeEmpty();
        f.SchemaShortnames.ShouldBeEmpty();
        f.Actions.ShouldBeEmpty();
    }

    [Fact]
    public void Absent_Json_Keys_Default_To_Empty()
    {
        // Contrast case: when keys are omitted entirely, STJ keeps the field
        // initializers — also empty, never null.
        var f = JsonSerializer.Deserialize("{}", DmartJsonContext.Default.EventFilter);

        f.ShouldNotBeNull();
        f!.Subpaths.ShouldBeEmpty();
        f.Actions.ShouldBeEmpty();
    }

    [Fact]
    public void Provided_Json_Values_Are_Preserved()
    {
        // The null-coalescing must not clobber real values.
        const string json =
            "{ \"subpaths\": { \"__all_spaces__\": [\"__all_subpaths__\"] }, " +
            "\"resource_types\": [\"content\"], " +
            "\"schema_shortnames\": [\"widget\"], \"actions\": [\"create\"] }";

        var f = JsonSerializer.Deserialize(json, DmartJsonContext.Default.EventFilter);

        f.ShouldNotBeNull();
        f!.Subpaths.ShouldContainKey("__all_spaces__");
        f.ResourceTypes.ShouldBe(new[] { "content" });
        f.SchemaShortnames.ShouldBe(new[] { "widget" });
        f.Actions.ShouldBe(new[] { "create" });
    }
}
