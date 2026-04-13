using System.Text.Json;
using Dmart.Models.Core;
using Dmart.Models.Json;

namespace Dmart.DataAdapters.Sql;

// Tiny helpers for round-tripping jsonb columns through DmartJsonContext.
// All "ToJsonb" return null for null input — so we can pass DBNull straight through.
public static class JsonbHelpers
{
    public static string? ToJsonb(Translation? t)
        => t is null ? null : JsonSerializer.Serialize(t, DmartJsonContext.Default.Translation);

    public static Translation? FromTranslation(string? json)
        => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize(json, DmartJsonContext.Default.Translation);

    public static string? ToJsonb(Payload? p)
        => p is null ? null : JsonSerializer.Serialize(p, DmartJsonContext.Default.Payload);

    public static Payload? FromPayload(string? json)
        => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize(json, DmartJsonContext.Default.Payload);

    public static string? ToJsonb(Reporter? r)
        => r is null ? null : JsonSerializer.Serialize(r, DmartJsonContext.Default.Reporter);

    public static Reporter? FromReporter(string? json)
        => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize(json, DmartJsonContext.Default.Reporter);

    public static string? ToJsonb(List<string>? list)
        => list is null ? null : JsonSerializer.Serialize(list, DmartJsonContext.Default.ListString);

    // For NOT NULL jsonb columns: returns "[]" for null/empty, never null.
    public static string ToJsonbList(List<string>? list)
        => list is null || list.Count == 0 ? "[]" : JsonSerializer.Serialize(list, DmartJsonContext.Default.ListString);

    public static string ToJsonbDict(Dictionary<string, List<string>>? d)
        => d is null || d.Count == 0 ? "{}" : JsonSerializer.Serialize(d, DmartJsonContext.Default.DictionaryStringListString);

    // For Spaces.languages — non-null jsonb array of language enum strings.
    public static string ToJsonbLanguagesNotNull(List<Models.Enums.Language>? langs)
        => langs is null || langs.Count == 0 ? "[]" : JsonSerializer.Serialize(langs, DmartJsonContext.Default.ListLanguage);

    public static List<string>? FromListString(string? json)
        => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize(json, DmartJsonContext.Default.ListString);

    public static string? ToJsonb(List<AclEntry>? acl)
        => acl is null ? null : JsonSerializer.Serialize(acl, DmartJsonContext.Default.ListAclEntry);

    public static List<AclEntry>? FromAclList(string? json)
        => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize(json, DmartJsonContext.Default.ListAclEntry);

    public static string? ToJsonb(List<Dictionary<string, object>>? rels)
        => rels is null ? null : JsonSerializer.Serialize(rels, DmartJsonContext.Default.ListDictionaryStringObject);

    public static List<Dictionary<string, object>>? FromRelationships(string? json)
        => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize(json, DmartJsonContext.Default.ListDictionaryStringObject);

    public static string? ToJsonb(Dictionary<string, List<string>>? d)
        => d is null ? null : JsonSerializer.Serialize(d, DmartJsonContext.Default.DictionaryStringListString);

    public static Dictionary<string, List<string>>? FromDictListString(string? json)
        => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize(json, DmartJsonContext.Default.DictionaryStringListString);

    public static string? ToJsonb(Dictionary<string, string>? d)
        => d is null ? null : JsonSerializer.Serialize(d, DmartJsonContext.Default.DictionaryStringString);

    public static Dictionary<string, string>? FromDictStringString(string? json)
        => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize(json, DmartJsonContext.Default.DictionaryStringString);

    public static string? ToJsonb(Dictionary<string, object>? d)
        => d is null ? null : JsonSerializer.Serialize(d, DmartJsonContext.Default.DictionaryStringObject);

    public static Dictionary<string, object>? FromDictStringObject(string? json)
        => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize(json, DmartJsonContext.Default.DictionaryStringObject);

    // Languages list — stored as JSONB array of language enum strings.
    public static string? ToJsonbLanguages(List<Models.Enums.Language>? langs)
        => langs is null ? null : JsonSerializer.Serialize(langs, DmartJsonContext.Default.ListLanguage);

    public static List<Models.Enums.Language>? FromLanguages(string? json)
        => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize(json, DmartJsonContext.Default.ListLanguage);

    // Maps C# enum to its [EnumMember] string. Used for resource_type, request_type
    // and other text columns that store dmart's "wire" enum values (e.g. "plugin_wrapper",
    // "english"). For PostgreSQL ENUM columns where dmart uses ISO codes (language: ar/en/...
    // — see EnumNameLower below), use the EnumNameLower variant instead.
    public static string EnumMember<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var name = value.ToString();
        var member = typeof(TEnum).GetField(name);
        var attr = member?.GetCustomAttributes(typeof(System.Runtime.Serialization.EnumMemberAttribute), false)
            .Cast<System.Runtime.Serialization.EnumMemberAttribute>().FirstOrDefault();
        return attr?.Value ?? name.ToLowerInvariant();
    }

    public static TEnum ParseEnumMember<TEnum>(string value) where TEnum : struct, Enum
    {
        foreach (var name in Enum.GetNames<TEnum>())
        {
            var member = typeof(TEnum).GetField(name);
            var attr = member?.GetCustomAttributes(typeof(System.Runtime.Serialization.EnumMemberAttribute), false)
                .Cast<System.Runtime.Serialization.EnumMemberAttribute>().FirstOrDefault();
            if (attr?.Value == value) return Enum.Parse<TEnum>(name);
        }
        return Enum.Parse<TEnum>(value, ignoreCase: true);
    }

    // For PG ENUM columns where dmart's database uses the C# member name
    // lowercased. Examples:
    //   * users.language — pg enum {ar,en,ku,fr,tr} matches C# {Ar,En,Ku,Fr,Tr}
    //   * users.type     — pg enum {web,mobile,bot} matches C# {Web,Mobile,Bot}
    public static string EnumNameLower<TEnum>(TEnum value) where TEnum : struct, Enum
        => value.ToString().ToLowerInvariant();

    public static TEnum ParseEnumNameLower<TEnum>(string value) where TEnum : struct, Enum
        => Enum.Parse<TEnum>(value, ignoreCase: true);
}
