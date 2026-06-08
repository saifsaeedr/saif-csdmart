using Dmart.Api.Managed;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Api;

// Pure coverage of RequestHandler.RetryOnShortnameCollisionAsync — the loop that
// makes auto-shortname creation collision-proof without lengthening the prefix
// (Python parity is 8 chars). No DB / no RNG: the attempt and the collision
// predicate are scripted, so the retry behaviour is deterministic.
public class AutoShortnameRetryTests
{
    [Fact]
    public async Task Retries_Until_A_NonColliding_Attempt_Succeeds()
    {
        var calls = 0;
        var result = await RequestHandler.RetryOnShortnameCollisionAsync(
            wasAuto: true,
            attempt: () => { calls++; return Task.FromResult(calls < 3 ? "COLLIDE" : "OK"); },
            isCollision: r => r == "COLLIDE");
        result.ShouldBe("OK");
        calls.ShouldBe(3); // collided twice, succeeded on the third mint
    }

    [Fact]
    public async Task Stops_After_MaxAttempts_And_Surfaces_The_Last_Result()
    {
        var calls = 0;
        var result = await RequestHandler.RetryOnShortnameCollisionAsync(
            wasAuto: true,
            attempt: () => { calls++; return Task.FromResult("COLLIDE"); },
            isCollision: r => r == "COLLIDE",
            maxAttempts: 4);
        result.ShouldBe("COLLIDE");
        calls.ShouldBe(4); // bounded — no infinite loop, and the error is surfaced
    }

    [Fact]
    public async Task Does_Not_Retry_A_Caller_Supplied_Duplicate()
    {
        var calls = 0;
        var result = await RequestHandler.RetryOnShortnameCollisionAsync(
            wasAuto: false, // explicit shortname — a duplicate is a genuine error
            attempt: () => { calls++; return Task.FromResult("COLLIDE"); },
            isCollision: r => r == "COLLIDE");
        result.ShouldBe("COLLIDE");
        calls.ShouldBe(1);
    }

    [Fact]
    public async Task Does_Not_Retry_When_The_First_Attempt_Succeeds()
    {
        var calls = 0;
        var result = await RequestHandler.RetryOnShortnameCollisionAsync(
            wasAuto: true,
            attempt: () => { calls++; return Task.FromResult("OK"); },
            isCollision: r => r == "COLLIDE");
        result.ShouldBe("OK");
        calls.ShouldBe(1);
    }
}
