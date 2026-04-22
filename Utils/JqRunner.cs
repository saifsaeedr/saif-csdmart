using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dmart.Utils;

// Subprocess wrapper around the `jq` binary. Mirrors Python dmart's
// backend/data_adapters/sql/adapter.py:1803-1872, which shells out to jq
// when a join sub-query carries a jq_filter expression.
//
// Why a subprocess (and not a managed library): there is no AOT-compatible
// jq engine for .NET today. Shelling out matches Python dmart's behavior
// exactly and keeps parity with the wire contract without a custom
// implementation that would drift in semantics.
//
// Availability: `jq` must be on PATH. RPMs declare `Requires: jq`; the
// container image installs it via apk/dnf.
public static class JqRunner
{
    // Mirrors Python's blocklist (backend/models/api.py:23) — dangerous
    // builtins that can leak server state or read external files.
    //
    // `path(` needs the `\(` so a literal "path(" in a filter triggers the
    // rejection even if it appears inside an expression.
    private static readonly Regex DangerousBuiltins = new(
        @"\benv\b|\$ENV\b|\binput\b|\bdebug\b|\bstderr\b|\bpath\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public const int MaxFilterLength = 1024;

    public enum FailureKind
    {
        None = 0,
        // Filter rejected by validation (length or blocked builtin).
        Invalid,
        // jq binary is not on PATH / failed to start.
        JqMissing,
        // Subprocess did not finish within timeoutSeconds.
        Timeout,
        // jq exited non-zero (syntax error or runtime error in the filter).
        JqError,
    }

    public readonly record struct Result(FailureKind Failure, JsonElement? Output, string? Stderr);

    public readonly record struct RawResult(FailureKind Failure, byte[]? StdoutBytes, string? Stderr);

    /// <summary>Validate the filter expression against length and blocklist.
    /// Mirrors Python's Pydantic field_validator.</summary>
    public static bool ValidateFilter(string filter, out string? reason)
    {
        if (filter.Length > MaxFilterLength)
        {
            reason = $"jq_filter exceeds {MaxFilterLength} character limit";
            return false;
        }
        if (DangerousBuiltins.IsMatch(filter))
        {
            reason = "jq_filter contains disallowed builtins (env, input, debug, stderr, path)";
            return false;
        }
        reason = null;
        return true;
    }

    /// <summary>Run <c>jq -c &lt;filter&gt;</c> with the given UTF-8 JSON input.
    /// Returns the parsed stdout root element, or a failure kind.</summary>
    public static async Task<Result> RunAsync(
        string filter, byte[] inputJson, int timeoutSeconds, CancellationToken ct = default)
    {
        if (!ValidateFilter(filter, out _))
            return new Result(FailureKind.Invalid, null, null);

        var psi = new ProcessStartInfo
        {
            FileName = "jq",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(filter);

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("jq failed to start");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new Result(FailureKind.JqMissing, null, "jq binary not found on PATH");
        }
        catch (Exception ex)
        {
            return new Result(FailureKind.JqMissing, null, ex.Message);
        }

        using (proc)
        {
            // Pipe JSON onto stdin. jq reads, processes, emits on stdout.
            // When jq rejects the filter (bad syntax, blocked builtin) it
            // exits before consuming stdin — our write then hits an
            // IOException ("Pipe is broken"). That's expected; swallow it
            // and let the caller branch on jq's exit code instead.
            var stdinTask = Task.Run(async () =>
            {
                try
                {
                    await proc.StandardInput.BaseStream.WriteAsync(inputJson, ct);
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                try
                {
                    proc.StandardInput.Close();
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }, ct);

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return new Result(FailureKind.Timeout, null, "jq timed out");
            }

            await Task.WhenAll(stdinTask, stdoutTask, stderrTask);

            if (proc.ExitCode != 0)
                return new Result(FailureKind.JqError, null, stderrTask.Result);

            var stdout = stdoutTask.Result;
            if (string.IsNullOrWhiteSpace(stdout))
                return new Result(FailureKind.None, null, null);

            // jq -c emits one JSON value per line. Python wraps the filter with
            // `map( [ <expr> ] )` so the output is a single top-level array —
            // parse that directly. If jq emitted multiple values (unvectorized
            // filter), concatenate them into an array.
            try
            {
                var trimmed = stdout.Trim();
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    return new Result(FailureKind.None, JsonDocument.Parse(trimmed).RootElement.Clone(), null);
                }
                // Multi-line JSONL → wrap into an array.
                var sb = new StringBuilder("[");
                var first = true;
                foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = line.Trim();
                    if (t.Length == 0) continue;
                    if (!first) sb.Append(',');
                    sb.Append(t);
                    first = false;
                }
                sb.Append(']');
                return new Result(FailureKind.None, JsonDocument.Parse(sb.ToString()).RootElement.Clone(), null);
            }
            catch (JsonException ex)
            {
                return new Result(FailureKind.JqError, null, $"jq produced non-JSON output: {ex.Message}");
            }
        }
    }

    /// <summary>Same as <see cref="RunAsync"/> but returns jq's raw stdout bytes
    /// without parsing. Used by the top-level jq_filter path to write jq output
    /// directly into the response envelope via <c>Utf8JsonWriter.WriteRawValue</c>
    /// — saves a parse+reserialize round-trip.</summary>
    public static async Task<RawResult> RunRawAsync(
        string filter, byte[] inputJson, int timeoutSeconds, CancellationToken ct = default)
    {
        if (!ValidateFilter(filter, out _))
            return new RawResult(FailureKind.Invalid, null, null);

        var psi = new ProcessStartInfo
        {
            FileName = "jq",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(filter);

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("jq failed to start");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new RawResult(FailureKind.JqMissing, null, "jq binary not found on PATH");
        }
        catch (Exception ex)
        {
            return new RawResult(FailureKind.JqMissing, null, ex.Message);
        }

        using (proc)
        {
            var stdinTask = Task.Run(async () =>
            {
                try { await proc.StandardInput.BaseStream.WriteAsync(inputJson, ct); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                try { proc.StandardInput.Close(); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }, ct);

            using var stdoutMs = new MemoryStream();
            var stdoutTask = proc.StandardOutput.BaseStream.CopyToAsync(stdoutMs, ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return new RawResult(FailureKind.Timeout, null, "jq timed out");
            }

            await Task.WhenAll(stdinTask, stdoutTask, stderrTask);

            if (proc.ExitCode != 0)
                return new RawResult(FailureKind.JqError, null, stderrTask.Result);

            return new RawResult(FailureKind.None, stdoutMs.ToArray(), null);
        }
    }
}
