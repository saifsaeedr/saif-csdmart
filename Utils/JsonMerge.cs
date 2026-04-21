using System.Text.Json;

namespace Dmart.Utils;

// Mirrors Python's deep_update(old, patch) + remove_none_dict(result).
// - Recursively merges a patch object into an existing JSON object
// - Sending a property as null in the patch removes that key
// - Pre-existing null values in `existing` are also stripped
public static class JsonMerge
{
    public static JsonElement? DeepMergeAndStripNulls(JsonElement? existing, JsonElement patch)
    {
        // If the patch is not an object, just use it directly (same as Python).
        if (patch.ValueKind != JsonValueKind.Object)
            return patch.ValueKind == JsonValueKind.Null ? null : patch;

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();

            // Start with all existing properties
            if (existing?.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in existing.Value.EnumerateObject())
                {
                    if (patch.TryGetProperty(prop.Name, out var patchVal))
                    {
                        // Key exists in patch — merge or overwrite
                        if (patchVal.ValueKind == JsonValueKind.Null)
                            continue; // null in patch = remove the key
                        if (patchVal.ValueKind == JsonValueKind.Object && prop.Value.ValueKind == JsonValueKind.Object)
                        {
                            // Recursive deep merge
                            var merged = DeepMergeAndStripNulls(prop.Value, patchVal);
                            if (merged is not null)
                            {
                                writer.WritePropertyName(prop.Name);
                                merged.Value.WriteTo(writer);
                            }
                        }
                        else
                        {
                            writer.WritePropertyName(prop.Name);
                            patchVal.WriteTo(writer);
                        }
                    }
                    else
                    {
                        // Key only in existing — keep it (strip if null)
                        if (prop.Value.ValueKind != JsonValueKind.Null)
                        {
                            writer.WritePropertyName(prop.Name);
                            prop.Value.WriteTo(writer);
                        }
                    }
                }
            }

            // Add new keys from patch that don't exist in existing
            foreach (var prop in patch.EnumerateObject())
            {
                if (existing?.ValueKind == JsonValueKind.Object && existing.Value.TryGetProperty(prop.Name, out _))
                    continue; // Already handled above
                if (prop.Value.ValueKind == JsonValueKind.Null)
                    continue; // null = don't add
                writer.WritePropertyName(prop.Name);
                prop.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }
}
