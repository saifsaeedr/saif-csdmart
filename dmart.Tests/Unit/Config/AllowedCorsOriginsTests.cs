using Dmart.Config;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Config;

// Unit tests for the CSV-to-array parsing of the ALLOWED_CORS_ORIGINS setting.
// Kept separate from the middleware tests so we can verify the parse behavior
// without booting an HTTP host.
public class AllowedCorsOriginsTests
{
    [Fact]
    public void Empty_String_Returns_Empty_Array()
    {
        new DmartSettings { AllowedCorsOrigins = "" }
            .ParseAllowedCorsOrigins()
            .ShouldBeEmpty();
    }

    [Fact]
    public void Whitespace_Only_Returns_Empty_Array()
    {
        new DmartSettings { AllowedCorsOrigins = "   " }
            .ParseAllowedCorsOrigins()
            .ShouldBeEmpty();
    }

    [Fact]
    public void Single_Origin_Parses_To_One_Element()
    {
        new DmartSettings { AllowedCorsOrigins = "https://app.example.com" }
            .ParseAllowedCorsOrigins()
            .ShouldBe(new[] { "https://app.example.com" });
    }

    [Fact]
    public void Csv_Splits_And_Trims()
    {
        new DmartSettings { AllowedCorsOrigins = "http://a.com, http://b.com ,https://c.com" }
            .ParseAllowedCorsOrigins()
            .ShouldBe(new[] { "http://a.com", "http://b.com", "https://c.com" });
    }

    [Fact]
    public void Empty_Slots_Are_Skipped()
    {
        new DmartSettings { AllowedCorsOrigins = "http://a.com,,http://b.com," }
            .ParseAllowedCorsOrigins()
            .ShouldBe(new[] { "http://a.com", "http://b.com" });
    }
}
