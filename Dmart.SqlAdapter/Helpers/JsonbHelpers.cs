using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Dmart.SqlAdapter.Helpers;

// JSONB read/write helpers used at the SQL boundary.
//
// dmart's schema stores most loose / variant data in JSONB columns (acl, tags,
// relationships, payload, displayname, description, roles, groups, ...). The
// server side has full source-gen JsonSerializerContext support; this module
// is host-agnostic so we lean on reflection-based serialization.
//
// AOT note: reflection-based STJ defeats AOT trimming. Consumers that need
// AOT support should pass their own JsonSerializerOptions with a
// JsonTypeInfoResolver — these helpers accept that path.

public static class JsonbHelpers
{
    private static readonly JsonSerializerOptions DefaultOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static T? ReadJsonb<T>(this NpgsqlDataReader r, int ordinal,
        JsonSerializerOptions? options = null) where T : class
    {
        if (r.IsDBNull(ordinal)) return null;
        var raw = r.GetString(ordinal);
        if (string.IsNullOrEmpty(raw) || raw == "null") return null;
        return JsonSerializer.Deserialize<T>(raw, options ?? DefaultOptions);
    }

    public static NpgsqlParameter ToJsonbParameter<T>(string name, T? value,
        JsonSerializerOptions? options = null)
    {
        var p = new NpgsqlParameter(name, NpgsqlDbType.Jsonb);
        p.Value = value is null
            ? DBNull.Value
            : JsonSerializer.Serialize(value, options ?? DefaultOptions);
        return p;
    }

    // Wire-format string for an enum that may carry [EnumMember(Value="…")].
    // Dmart.Models enums use snake_case wire values (e.g. ResourceType.PluginWrapper
    // → "plugin_wrapper", DataAsset → "data_asset"). Reading `[EnumMember]` is the
    // only way to get the right form without duplicating the mapping. Falls back
    // to the CLR name lower-cased when no attribute is set.
    public static string EnumMember<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var name = value.ToString();
        var field = typeof(TEnum).GetField(name, BindingFlags.Public | BindingFlags.Static);
        var attr = field?.GetCustomAttribute<EnumMemberAttribute>();
        return attr?.Value ?? name.ToLowerInvariant();
    }

    // Reverse mapping for resource_type column reads: "plugin_wrapper" → PluginWrapper.
    public static TEnum ParseEnumMember<TEnum>(string wire) where TEnum : struct, Enum
    {
        foreach (var f in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var attr = f.GetCustomAttribute<EnumMemberAttribute>();
            if (attr?.Value == wire || string.Equals(f.Name, wire, StringComparison.OrdinalIgnoreCase))
                return (TEnum)f.GetValue(null)!;
        }
        return default;
    }
}
