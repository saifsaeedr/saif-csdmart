using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace dmart.Tests.Unit;

public sealed class RequestForceFieldTests
{
    [Fact]
    public void Force_Parses_From_Json_When_Present()
    {
        var req = JsonSerializer.Deserialize(
            """{"request_type":"delete","space_name":"x","records":[],"force":true}""",
            DmartJsonContext.Default.Request)!;
        req.Force.ShouldBeTrue();
    }

    [Fact]
    public void Force_Defaults_False_When_Absent()
    {
        var req = JsonSerializer.Deserialize(
            """{"request_type":"delete","space_name":"x","records":[]}""",
            DmartJsonContext.Default.Request)!;
        req.Force.ShouldBeFalse();
    }
}
