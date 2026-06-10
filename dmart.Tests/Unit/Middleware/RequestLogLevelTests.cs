using Dmart.Middleware;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Middleware;

// Request-log level policy. Production data (1M-user deployment) showed 77%
// of WARNING volume was routine 404s — users fetching their own never-
// uploaded avatar. Policy: 5xx are errors, auth/abuse signals (401/403/429)
// stay warnings, every other 4xx is a routine client error logged at
// Information, and a client abort is always Information.
public class RequestLogLevelTests
{
    [Theory]
    [InlineData(200, LogLevel.Information)]
    [InlineData(201, LogLevel.Information)]
    [InlineData(304, LogLevel.Information)]
    [InlineData(400, LogLevel.Information)]   // routine client error
    [InlineData(404, LogLevel.Information)]   // missing avatar et al.
    [InlineData(409, LogLevel.Information)]
    [InlineData(422, LogLevel.Information)]
    [InlineData(401, LogLevel.Warning)]       // failed auth — abuse signal
    [InlineData(403, LogLevel.Warning)]       // denied — abuse signal
    [InlineData(429, LogLevel.Warning)]       // rate limited — abuse signal
    [InlineData(500, LogLevel.Error)]
    [InlineData(503, LogLevel.Error)]
    public void MapLevel_Maps_Status_To_Policy(int status, LogLevel expected)
        => RequestLoggingMiddleware.MapLevel(status, clientAborted: false).ShouldBe(expected);

    [Fact]
    public void MapLevel_ClientAborted_Is_Always_Information()
    {
        RequestLoggingMiddleware.MapLevel(500, clientAborted: true).ShouldBe(LogLevel.Information);
        RequestLoggingMiddleware.MapLevel(401, clientAborted: true).ShouldBe(LogLevel.Information);
    }
}
