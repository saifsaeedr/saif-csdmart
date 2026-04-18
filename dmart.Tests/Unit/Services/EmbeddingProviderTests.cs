using Dmart.Services;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Unit tests for the deterministic `mock://` embedder. We don't exercise
// the HTTP path here — that requires a real (or mocked) server.
public sealed class EmbeddingProviderTests
{
    [Fact]
    public void MockEmbed_Is_Deterministic()
    {
        var a = EmbeddingProvider.MockEmbed("hello world");
        var b = EmbeddingProvider.MockEmbed("hello world");
        a.ShouldBe(b);
    }

    [Fact]
    public void MockEmbed_DifferentInputs_ProduceDifferentVectors()
    {
        var a = EmbeddingProvider.MockEmbed("apple");
        var b = EmbeddingProvider.MockEmbed("banana");
        a.SequenceEqual(b).ShouldBeFalse();
    }

    [Fact]
    public void MockEmbed_ReturnsUnitVector()
    {
        var v = EmbeddingProvider.MockEmbed("anything");
        var norm = Math.Sqrt(v.Sum(x => x * (double)x));
        norm.ShouldBe(1.0, tolerance: 1e-5);
        v.Length.ShouldBe(128);
    }

    [Fact]
    public void FormatVectorLiteral_MatchesPgVectorSyntax()
    {
        var lit = EmbeddingProvider.FormatVectorLiteral(new[] { 1.0f, -0.5f, 0.25f });
        lit.ShouldStartWith("[");
        lit.ShouldEndWith("]");
        lit.Split(',').Length.ShouldBe(3);
    }
}
