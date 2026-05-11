using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Dmart;

// Console JSON formatter without the State block. The stock
// JsonConsoleFormatter emits a State object containing the resolved structured
// properties AND a "{OriginalFormat}" key carrying the raw message template,
// which is noisy when tailing logs by eye. We drop the entire block — Message
// already carries the rendered string. Aggregators that group by template
// won't be able to roll up per-template counts; aggregators that group by
// Category + level still work fine.
internal sealed class DmartJsonConsoleFormatter : ConsoleFormatter
{
    public const string FormatterName = "dmart-json";

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    public DmartJsonConsoleFormatter() : base(FormatterName) { }

    public override void Write<TState>(in LogEntry<TState> entry,
        IExternalScopeProvider? scopeProvider, TextWriter writer)
    {
        var message = entry.Formatter?.Invoke(entry.State, entry.Exception);
        if (message is null && entry.Exception is null) return;

        // Match LogSink: prepend `[<shortname>] ` for plugin-category log
        // lines so the terminal stream matches the JSONL file. Idempotent —
        // a message already starting with `[` is left alone.
        if (!string.IsNullOrEmpty(message) && message[0] != '[')
        {
            var tag = LogSink.PluginTagForCategory(entry.Category);
            if (tag is not null) message = $"[{tag}] {message}";
        }

        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteString("Timestamp", TimeUtils.Now().ToString("yyyy-MM-ddTHH:mm:ss.fff"));
            json.WriteString("LogLevel", entry.LogLevel.ToString());
            json.WriteString("Category", entry.Category);
            if (entry.EventId.Id != 0) json.WriteNumber("EventId", entry.EventId.Id);
            if (!string.IsNullOrEmpty(message)) json.WriteString("Message", message);
            if (entry.Exception is not null) json.WriteString("Exception", entry.Exception.ToString());
            json.WriteEndObject();
        }
        writer.Write(Encoding.UTF8.GetString(stream.ToArray()));
        writer.Write(Environment.NewLine);
    }
}
