using System.Collections.Concurrent;
using System.Text.Json;

namespace Dmart;

// Minimal file logger for dmart. Writes log lines to a file, optionally as
// JSON (matching Python's .ljson.log format). AOT-safe — no reflection.
//
// Usage: builder.Logging.AddProvider(new FileLoggerProvider("logs/dmart.log", "json"));
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly string _format;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public FileLoggerProvider(string path, string format)
    {
        _path = path;
        _format = format;
        _writer = new StreamWriter(path, append: true) { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    public void Dispose()
    {
        _writer.Dispose();
        _loggers.Clear();
    }

    internal void Write(string category, LogLevel level, string message)
    {
        lock (_lock)
        {
            if (string.Equals(_format, "json", StringComparison.OrdinalIgnoreCase))
            {
                // Manual JSON to avoid anonymous type serialization (AOT-unsafe).
                var ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                var lvl = level.ToString();
                var esc = message.Replace("\\", "\\\\").Replace("\"", "\\\"");
                var cat = category.Replace("\\", "\\\\").Replace("\"", "\\\"");
                _writer.WriteLine($"{{\"timestamp\":\"{ts}\",\"level\":\"{lvl}\",\"category\":\"{cat}\",\"message\":\"{esc}\"}}");
            }
            else
            {
                _writer.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} [{level}] {category}: {message}");
            }
        }
    }
}

internal sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = formatter(state, exception);
        if (exception is not null) message += $" | {exception.GetType().Name}: {exception.Message}";
        provider.Write(category, logLevel, message);
    }
}
