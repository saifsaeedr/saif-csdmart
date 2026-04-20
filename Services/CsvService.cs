using System.Globalization;
using System.Text;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Utils;

namespace Dmart.Services;

// CSV import/export. Mirrors dmart Python's behavior:
//   * Export: run a Query, flatten record attributes (including nested payload.body),
//     compute the union of column keys, emit RFC 4180 CSV with the columns
//     ["resource_type", "shortname", "subpath", "uuid", ...flattened attributes].
//   * Import: parse a CSV file, build a Record per row using the column headers as
//     attribute keys, schema-validate each, and create via EntryService.
public sealed class CsvService(QueryService queries, EntryService entries)
{
    public async Task<Stream> ExportAsync(Query q, string? actor, CancellationToken ct = default)
    {
        var response = await queries.ExecuteAsync(q, actor, ct);
        var records = response.Records ?? new List<Record>();

        // Step 1: flatten each row.
        var flattened = records.Select(r =>
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["resource_type"] = JsonbHelpers.EnumMember(r.ResourceType),
                ["shortname"]     = r.Shortname,
                ["subpath"]       = r.Subpath,
                ["uuid"]          = r.Uuid ?? "",
            };
            if (r.Attributes is not null)
                FlattenInto(r.Attributes, "", dict);
            return dict;
        }).ToList();

        // Step 2: compute union of all keys (preserving the canonical first-four order).
        var canonical = new[] { "resource_type", "shortname", "subpath", "uuid" };
        var extraKeys = flattened.SelectMany(d => d.Keys)
            .Where(k => !canonical.Contains(k, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var allKeys = canonical.Concat(extraKeys).ToArray();

        // Step 3: emit RFC 4180 CSV.
        var sb = new StringBuilder();
        sb.Append(string.Join(",", allKeys.Select(EscapeField)));
        sb.Append("\r\n");
        foreach (var row in flattened)
        {
            sb.Append(string.Join(",", allKeys.Select(k =>
                row.TryGetValue(k, out var v) ? EscapeField(v) : "")));
            sb.Append("\r\n");
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return new MemoryStream(bytes);
    }

    public async Task<Response> ImportAsync(
        string spaceName, string subpath, ResourceType resourceType, string? schemaShortname,
        Stream csv, string? actor, CancellationToken ct = default)
    {
        using var reader = new StreamReader(csv, Encoding.UTF8);
        var headerLine = await reader.ReadLineAsync(ct);
        if (string.IsNullOrEmpty(headerLine))
            return Response.Fail(InternalErrorCode.MISSING_DATA, "csv has no header row", ErrorTypes.Request);

        var headers = ParseCsvLine(headerLine);
        var inserted = 0;
        var failed = new List<Dictionary<string, object>>();
        var rowNumber = 0;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            rowNumber++;
            if (rowNumber > 100_000)
                return Response.Fail(InternalErrorCode.INVALID_DATA,
                    "CSV exceeds maximum of 100,000 rows", "request");
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = ParseCsvLine(line);
            if (fields.Count != headers.Count)
            {
                failed.Add(new() { ["row"] = rowNumber, ["error"] = $"expected {headers.Count} fields, got {fields.Count}" });
                continue;
            }

            // Build attributes from the headers + values.
            var rowDict = new Dictionary<string, object>();
            for (var i = 0; i < headers.Count; i++)
                rowDict[headers[i]] = fields[i];

            // shortname column is required (or auto-generate)
            var shortname = rowDict.TryGetValue("shortname", out var sn) ? sn?.ToString() ?? "" : "";
            if (string.IsNullOrEmpty(shortname))
                shortname = $"row-{Guid.NewGuid():N}".Substring(0, 12);

            // Build the entry's payload.body from the remaining columns.
            var bodyDict = rowDict
                .Where(kv => !string.Equals(kv.Key, "shortname", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            var bodyJson = JsonSerializer.Serialize(bodyDict, DmartJsonContext.Default.DictionaryStringObject);
            var bodyEl = JsonDocument.Parse(bodyJson).RootElement.Clone();

            var entry = new Entry
            {
                Uuid = Guid.NewGuid().ToString(),
                Shortname = shortname,
                SpaceName = spaceName,
                Subpath = subpath,
                ResourceType = resourceType,
                OwnerShortname = actor ?? "anonymous",
                IsActive = true,
                Payload = new Payload
                {
                    ContentType = ContentType.Json,
                    SchemaShortname = schemaShortname,
                    Body = bodyEl,
                },
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };

            var result = await entries.CreateAsync(entry, actor, ct);
            if (result.IsOk) inserted++;
            else failed.Add(new()
            {
                ["row"] = rowNumber,
                ["shortname"] = shortname,
                ["error"] = result.ErrorMessage ?? "unknown",
                ["code"] = result.ErrorCode,
            });
        }

        return Response.Ok(attributes: new()
        {
            ["inserted"] = inserted,
            ["failed_count"] = failed.Count,
            ["failed"] = failed,
        });
    }

    // ----- helpers -----

    private static void FlattenInto(Dictionary<string, object> source, string prefix, Dictionary<string, string> dest)
    {
        foreach (var (k, v) in source)
        {
            var key = string.IsNullOrEmpty(prefix) ? k : $"{prefix}.{k}";
            switch (v)
            {
                case null:
                    dest[key] = "";
                    break;
                case string s:
                    dest[key] = s;
                    break;
                case bool b:
                    dest[key] = b ? "true" : "false";
                    break;
                case JsonElement el:
                    FlattenJsonElement(el, key, dest);
                    break;
                case Dictionary<string, object> nested:
                    FlattenInto(nested, key, dest);
                    break;
                case Translation t:
                    if (!string.IsNullOrEmpty(t.En)) dest[$"{key}.en"] = t.En;
                    if (!string.IsNullOrEmpty(t.Ar)) dest[$"{key}.ar"] = t.Ar;
                    if (!string.IsNullOrEmpty(t.Ku)) dest[$"{key}.ku"] = t.Ku;
                    break;
                case Payload p:
                    dest[key] = JsonSerializer.Serialize(p, DmartJsonContext.Default.Payload);
                    break;
                case List<string> stringList:
                    dest[key] = string.Join("|", stringList);
                    break;
                case string[] stringArr:
                    dest[key] = string.Join("|", stringArr);
                    break;
                case List<AclEntry> aclList:
                    dest[key] = JsonSerializer.Serialize(aclList, DmartJsonContext.Default.ListAclEntry);
                    break;
                case List<Dictionary<string, object>> dictList:
                    dest[key] = JsonSerializer.Serialize(dictList, DmartJsonContext.Default.ListDictionaryStringObject);
                    break;
                default:
                    dest[key] = ConvertScalar(v);
                    break;
            }
        }
    }

    private static void FlattenJsonElement(JsonElement el, string key, Dictionary<string, string> dest)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                    FlattenJsonElement(prop.Value, $"{key}.{prop.Name}", dest);
                break;
            case JsonValueKind.Array:
                // Join scalar arrays with `|`; complex arrays serialize as JSON.
                var sb = new StringBuilder();
                var allScalar = true;
                var first = true;
                foreach (var item in el.EnumerateArray())
                {
                    if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array) { allScalar = false; break; }
                    if (!first) sb.Append('|');
                    sb.Append(item.ValueKind switch
                    {
                        JsonValueKind.String => item.GetString(),
                        JsonValueKind.True   => "true",
                        JsonValueKind.False  => "false",
                        JsonValueKind.Null   => "",
                        _                    => item.GetRawText(),
                    });
                    first = false;
                }
                dest[key] = allScalar ? sb.ToString() : el.GetRawText();
                break;
            case JsonValueKind.String:
                dest[key] = el.GetString() ?? "";
                break;
            case JsonValueKind.True:
                dest[key] = "true"; break;
            case JsonValueKind.False:
                dest[key] = "false"; break;
            case JsonValueKind.Null:
                dest[key] = ""; break;
            default:
                dest[key] = el.GetRawText(); break;
        }
    }

    private static string ConvertScalar(object? v) => v switch
    {
        null      => "",
        string s  => s,
        bool b    => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _         => v.ToString() ?? "",
    };

    private static string EscapeField(string s)
    {
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            return $"\"{s.Replace("\"", "\"\"")}\"";
        return s;
    }

    // RFC 4180 CSV line parser — handles quoted fields with embedded commas, newlines,
    // and escaped quotes (`""`). Note: doesn't support records that span lines; use
    // a streaming reader if your CSV has multi-line quoted fields.
    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(ch);
            }
            else
            {
                if (ch == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                else if (ch == '"') inQuotes = true;
                else sb.Append(ch);
            }
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
