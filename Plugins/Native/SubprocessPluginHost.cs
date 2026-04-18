using System.Diagnostics;
using System.Text;

namespace Dmart.Plugins.Native;

// Manages a plugin subprocess. Communicates via stdin/stdout JSON lines.
// If the process crashes, it's automatically respawned on the next call.
//
// Protocol (one JSON line per message):
//   dmart → plugin stdin:  {"type":"hook","event":{...}}
//   plugin → dmart stdout: {"status":"ok"} or {"status":"error","message":"..."}
//
//   dmart → plugin stdin:  {"type":"request","request":{...}}
//   plugin → dmart stdout: {"status":"success","attributes":{...}}
//
//   dmart → plugin stdin:  {"type":"info"}
//   plugin → dmart stdout: {"shortname":"x","type":"hook|api",...}
internal sealed class SubprocessPluginHost : IDisposable
{
    private readonly string _executablePath;
    private readonly string _workingDir;
    private readonly object _lock = new();
    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;

    public string Shortname { get; }

    public SubprocessPluginHost(string executablePath, string shortname)
    {
        _executablePath = executablePath;
        _workingDir = Path.GetDirectoryName(executablePath) ?? ".";
        Shortname = shortname;
        EnsureRunning();
    }

    public string SendAndReceive(string jsonLine)
    {
        lock (_lock)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    EnsureRunning();
                    _stdin!.WriteLine(jsonLine);
                    _stdin.Flush();
                    var response = _stdout!.ReadLine();
                    if (response is not null) return response;
                    // null = process died, retry
                    Kill();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"SUBPROCESS_PLUGIN_ERROR: {Shortname} attempt={attempt + 1}/2 {ex.GetType().Name}: {ex.Message}");
                    Kill();
                    if (attempt == 1) throw;
                }
            }
            return "{\"status\":\"error\",\"message\":\"plugin process unresponsive\"}";
        }
    }

    private void EnsureRunning()
    {
        if (_process is not null && !_process.HasExited) return;

        Kill(); // cleanup any dead process

        var psi = new ProcessStartInfo
        {
            FileName = _executablePath,
            WorkingDirectory = _workingDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start plugin: {_executablePath}");
        _stdin = _process.StandardInput;
        _stdin.AutoFlush = false;
        _stdout = _process.StandardOutput;

        // Drain stderr to console in background (plugin debug output)
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_process.HasExited)
                {
                    var line = await _process.StandardError.ReadLineAsync();
                    if (line is not null)
                        Console.Error.WriteLine($"[{Shortname}] {line}");
                }
            }
            catch { /* process exited */ }
        });

        Console.WriteLine($"SUBPROCESS_PLUGIN_STARTED: {Shortname} pid={_process.Id}");
    }

    private void Kill()
    {
        try
        {
            _stdin?.Dispose();
            _stdout?.Dispose();
            if (_process is { HasExited: false })
            {
                _process.Kill();
                _process.WaitForExit(1000);
            }
            _process?.Dispose();
        }
        catch { /* ignore cleanup errors */ }
        finally
        {
            _process = null;
            _stdin = null;
            _stdout = null;
        }
    }

    // Graceful shutdown: close stdin so the subprocess's `for line in sys.stdin`
    // (or equivalent) returns EOF and exits on its own, give it up to 500ms,
    // then kill if it's still around. Used by IHostApplicationLifetime's
    // ApplicationStopping hook so non-terminal shutdowns (SIGTERM from
    // systemd, docker stop, host.StopAsync()) never have to fall through to
    // Kill().
    public void Shutdown()
    {
        lock (_lock)
        {
            if (_process is null) return;
            try
            {
                _stdin?.Dispose();  // closes the pipe → subprocess sees EOF
                if (_process is { HasExited: false })
                    _process.WaitForExit(500);
            }
            catch { /* ignore — fall through to Kill */ }
            Kill();
        }
    }

    public void Dispose() => Kill();
}
