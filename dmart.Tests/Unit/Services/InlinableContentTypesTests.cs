using Dmart.Services;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Locks in the InlinableContentTypes whitelist that ImportExportService.
// InlinePayloadBody/InlinePayloadBodyAsync consult before reading a sibling
// body file into payload.body. The set must:
//   - Cover everything MaybeExternalizePayloadBodyAsync (this file) writes
//     out: json, html, text, markdown — round-trip parity for our exporter.
//   - Cover what dmart-Python's exporter externalizes: csv, jsonl — so a
//     Python-exported zip imported by C# doesn't silently lose body data.
//   - NOT cover binary content types (image, media, python source, comment,
//     reaction). Inlining a PNG/MP3 through StreamReader corrupts it; the
//     filename must stay in payload.body so the bytes can be fetched from
//     the on-disk attachment.
//
// If MaybeExternalizePayloadBodyAsync grows a new `case`, add the matching
// content_type to InlinableContentTypes AND add it here so the regression
// pin updates with intent.
public sealed class InlinableContentTypesTests
{
    [Theory]
    [InlineData("json")]
    [InlineData("text")]
    [InlineData("html")]
    [InlineData("markdown")]
    [InlineData("csv")]
    [InlineData("jsonl")]
    public void Whitelisted_TextLike_ContentTypes_Are_Inlinable(string contentType)
        => ImportExportService.InlinableContentTypes.Contains(contentType).ShouldBeTrue(
            $"'{contentType}' must be inlinable for round-trip parity with the exporter");

    [Theory]
    // Binary blobs — must stay as filename string in payload.body.
    [InlineData("image")]
    [InlineData("media")]
    [InlineData("audio")]
    [InlineData("video")]
    [InlineData("pdf")]
    // Python source is currently kept as on-disk attachment, not inlined.
    [InlineData("python")]
    // Comment/reaction bodies are tiny strings stored inline in meta —
    // never externalized, so inclusion would be dead code and a future
    // refactor that DOES externalize them would silently double-encode.
    [InlineData("comment")]
    [InlineData("reaction")]
    public void NonTextLike_ContentTypes_Are_Not_Inlinable(string contentType)
        => ImportExportService.InlinableContentTypes.Contains(contentType).ShouldBeFalse(
            $"'{contentType}' must NOT be inlinable — would corrupt binary bytes "
            + "or double-encode inline strings");

    [Fact]
    public void ContentType_Matching_Is_Case_Insensitive()
    {
        // The lookup site lowercases via ToLowerInvariant(), but the
        // HashSet's OrdinalIgnoreCase comparer is the load-bearing piece —
        // pin it so a future refactor of the lookup site doesn't break
        // the contract.
        ImportExportService.InlinableContentTypes.Contains("JSON").ShouldBeTrue();
        ImportExportService.InlinableContentTypes.Contains("Json").ShouldBeTrue();
        ImportExportService.InlinableContentTypes.Contains("MarkDown").ShouldBeTrue();
    }
}
