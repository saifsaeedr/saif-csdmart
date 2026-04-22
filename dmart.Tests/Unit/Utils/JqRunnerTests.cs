using System.Text;
using System.Text.Json;
using Dmart.Utils;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Utils;

public class JqRunnerTests
{
    // ---- ValidateFilter ----

    [Fact]
    public void ValidateFilter_Accepts_Simple_Identity()
    {
        JqRunner.ValidateFilter(".", out var reason).ShouldBeTrue();
        reason.ShouldBeNull();
    }

    [Theory]
    [InlineData("env")]
    [InlineData("$ENV.FOO")]
    [InlineData("input")]
    [InlineData("debug")]
    [InlineData("stderr")]
    [InlineData("path(.foo)")]
    public void ValidateFilter_Rejects_Blocked_Builtins(string filter)
    {
        JqRunner.ValidateFilter(filter, out var reason).ShouldBeFalse();
        reason.ShouldContain("disallowed builtins");
    }

    [Fact]
    public void ValidateFilter_Rejects_Oversize_Filter()
    {
        var bigFilter = new string('.', JqRunner.MaxFilterLength + 1);
        JqRunner.ValidateFilter(bigFilter, out var reason).ShouldBeFalse();
        reason.ShouldContain("character limit");
    }

    // ---- RunAsync — these exercise the real jq binary. They'll only run
    // when jq is on PATH (CI images and dev boxes typically have it). We
    // skip gracefully if it's missing so the suite stays green on
    // jq-less build agents.

    private static async Task<bool> JqAvailableAsync()
    {
        var r = await JqRunner.RunAsync(".", Encoding.UTF8.GetBytes("[]"), timeoutSeconds: 2);
        return r.Failure != JqRunner.FailureKind.JqMissing;
    }

    [Fact]
    public async Task RunAsync_Identity_Returns_Input()
    {
        if (!await JqAvailableAsync()) return;
        var r = await JqRunner.RunAsync(".", Encoding.UTF8.GetBytes("[[1,2],[3]]"), timeoutSeconds: 2);
        r.Failure.ShouldBe(JqRunner.FailureKind.None);
        r.Output.ShouldNotBeNull();
        r.Output!.Value.ValueKind.ShouldBe(JsonValueKind.Array);
        r.Output!.Value.GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task RunAsync_Vectorized_Shape_Produces_Aligned_Output()
    {
        if (!await JqAvailableAsync()) return;
        // The filter operates on the *inner* list (matches for one base
        // record), not on individual records. Users supply `.[] | ...` to
        // iterate. QueryService wraps as `map( [ <filter> ] )` so the outer
        // layer is aligned 1:1 with base records.
        //
        // Here: input [[{name:a},{name:b}], [{name:c}]] with filter
        // `.[] | .name` produces outer array of 2 slices.
        var input = Encoding.UTF8.GetBytes("""[[{"name":"a"},{"name":"b"}],[{"name":"c"}]]""");
        var r = await JqRunner.RunAsync("map( [ .[] | .name ] )", input, timeoutSeconds: 2);
        r.Failure.ShouldBe(JqRunner.FailureKind.None);
        r.Output.ShouldNotBeNull();
        r.Output!.Value.GetArrayLength().ShouldBe(2);
        r.Output!.Value[0].GetArrayLength().ShouldBe(2);   // a, b
        r.Output!.Value[1].GetArrayLength().ShouldBe(1);   // c
    }

    [Fact]
    public async Task RunAsync_Bad_Filter_Returns_JqError()
    {
        if (!await JqAvailableAsync()) return;
        var r = await JqRunner.RunAsync(".[syntax error", Encoding.UTF8.GetBytes("[]"), timeoutSeconds: 2);
        r.Failure.ShouldBe(JqRunner.FailureKind.JqError);
    }

    [Fact]
    public async Task RunAsync_Blocked_Builtin_Returns_Invalid_Without_Shelling_Out()
    {
        var r = await JqRunner.RunAsync("env", Encoding.UTF8.GetBytes("[]"), timeoutSeconds: 2);
        r.Failure.ShouldBe(JqRunner.FailureKind.Invalid);
    }

    // ---- RunRawAsync ----

    [Fact]
    public async Task RunRawAsync_Returns_Raw_Stdout_Bytes()
    {
        if (!await JqAvailableAsync()) return;
        var input = Encoding.UTF8.GetBytes("""[{"name":"a"},{"name":"b"}]""");
        var r = await JqRunner.RunRawAsync("map(.name)", input, timeoutSeconds: 2);
        r.Failure.ShouldBe(JqRunner.FailureKind.None);
        r.StdoutBytes.ShouldNotBeNull();
        // jq -c emits a compact JSON array on one line.
        Encoding.UTF8.GetString(r.StdoutBytes!).Trim().ShouldBe("""["a","b"]""");
    }

    [Fact]
    public async Task RunRawAsync_Bad_Filter_Returns_JqError()
    {
        if (!await JqAvailableAsync()) return;
        var r = await JqRunner.RunRawAsync(".[syntax error", Encoding.UTF8.GetBytes("[]"), timeoutSeconds: 2);
        r.Failure.ShouldBe(JqRunner.FailureKind.JqError);
    }

    [Fact]
    public async Task RunRawAsync_Blocked_Builtin_Returns_Invalid_Without_Shelling_Out()
    {
        var r = await JqRunner.RunRawAsync("env", Encoding.UTF8.GetBytes("[]"), timeoutSeconds: 2);
        r.Failure.ShouldBe(JqRunner.FailureKind.Invalid);
    }
}
