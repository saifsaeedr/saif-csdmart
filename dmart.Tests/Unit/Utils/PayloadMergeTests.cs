using System.Text.Json;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Utils;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Utils;

// Pins PayloadMerge.MergeBody's update semantics: deep-merge the patch body and
// HONOR a patch-declared schema_shortname / content_type (the behavior the
// schema gate on the update paths relies on), while leaving the resource
// untouched when the patch carries nothing usable.
public class PayloadMergeTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    private static Payload Existing(
        string body = "{\"a\":1}", string? schema = "existing_schema",
        ContentType contentType = ContentType.Json)
        => new Payload { ContentType = contentType, SchemaShortname = schema, Body = Json(body) };

    [Fact]
    public void Empty_Patch_Returns_Existing_Unchanged()
    {
        var existing = Existing();
        PayloadMerge.MergeBody(existing, Json("{}")).ShouldBeSameAs(existing);
    }

    [Fact]
    public void Null_PayloadRaw_Returns_Existing_Unchanged()
    {
        var existing = Existing();
        PayloadMerge.MergeBody(existing, null).ShouldBeSameAs(existing);
    }

    [Fact]
    public void Explicit_Null_Body_With_No_Metadata_Returns_Existing_Unchanged()
    {
        var existing = Existing();
        PayloadMerge.MergeBody(existing, Json("{\"body\":null}")).ShouldBeSameAs(existing);
    }

    [Fact]
    public void Body_Only_Patch_Deep_Merges_And_Keeps_Existing_Schema_And_ContentType()
    {
        var existing = Existing(body: "{\"a\":1}", schema: "existing_schema", contentType: ContentType.Json);
        var result = PayloadMerge.MergeBody(existing, Json("{\"body\":{\"b\":2}}"));

        result.ShouldNotBeNull();
        result!.SchemaShortname.ShouldBe("existing_schema");   // not cleared by a body-only patch
        result.ContentType.ShouldBe(ContentType.Json);
        result.Body!.Value.GetProperty("a").GetInt32().ShouldBe(1);   // existing key retained
        result.Body!.Value.GetProperty("b").GetInt32().ShouldBe(2);   // patch key merged in
    }

    [Fact]
    public void Patch_Declaring_Schema_Shortname_Is_Honored_Even_Without_A_Body()
    {
        var existing = Existing(schema: "old");
        var result = PayloadMerge.MergeBody(existing, Json("{\"schema_shortname\":\"new\"}"));

        result.ShouldNotBeNull();
        result!.SchemaShortname.ShouldBe("new");
        result.Body!.Value.GetProperty("a").GetInt32().ShouldBe(1);   // body untouched
    }

    [Fact]
    public void Patch_Declaring_ContentType_Is_Honored_And_Parsed_Case_Insensitively()
    {
        var existing = Existing(contentType: ContentType.Json);
        PayloadMerge.MergeBody(existing, Json("{\"content_type\":\"MARKDOWN\"}"))!
            .ContentType.ShouldBe(ContentType.Markdown);
    }

    [Fact]
    public void Patch_With_Unrecognized_ContentType_Defaults_To_Json()
    {
        var existing = Existing(contentType: ContentType.Text);
        PayloadMerge.MergeBody(existing, Json("{\"content_type\":\"not-a-real-type\"}"))!
            .ContentType.ShouldBe(ContentType.Json);
    }

    [Fact]
    public void Patch_With_No_Existing_Payload_Creates_A_Default_Json_Payload()
    {
        var result = PayloadMerge.MergeBody(null, Json("{\"body\":{\"a\":1}}"));

        result.ShouldNotBeNull();
        result!.ContentType.ShouldBe(ContentType.Json);
        result.Body!.Value.GetProperty("a").GetInt32().ShouldBe(1);
    }

    [Fact]
    public void Patch_Null_Body_Property_Removes_The_Key_From_The_Merged_Body()
    {
        // Deep-merge parity: a property sent as null in the patch body removes it.
        var existing = Existing(body: "{\"a\":1,\"b\":2}");
        var result = PayloadMerge.MergeBody(existing, Json("{\"body\":{\"b\":null}}"));

        result!.Body!.Value.TryGetProperty("b", out _).ShouldBeFalse();
        result.Body!.Value.GetProperty("a").GetInt32().ShouldBe(1);
    }
}
