using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dmart.Models.Enums;

namespace Dmart.Models.Json;

// Source-gen JSON in .NET 10 doesn't honor [EnumMember] or apply PropertyNamingPolicy
// to enum values, so we provide explicit per-enum converters that read [EnumMember]
// at first use and then look up via cached arrays. AOT-safe: each typed converter
// is concrete, no open generics emitted to attributes.
public abstract class EnumMemberConverterBase<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    private static readonly (TEnum Value, string Name)[] Map = BuildMap();

    private static (TEnum, string)[] BuildMap()
    {
        // netstandard2.1 predates Enum.GetValues<TEnum>(); the non-generic
        // overload returns a weakly-typed Array. Cast to TEnum[] uniformly.
        var values = (TEnum[])Enum.GetValues(typeof(TEnum));
        var result = new (TEnum, string)[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var v = values[i];
            var member = typeof(TEnum).GetField(v.ToString());
            var attr = member?.GetCustomAttribute<EnumMemberAttribute>();
            result[i] = (v, attr?.Value ?? v.ToString().ToLowerInvariant());
        }
        return result;
    }

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (s is null) throw new JsonException("expected enum string");
        foreach (var (value, name) in Map)
            if (string.Equals(name, s, StringComparison.OrdinalIgnoreCase)) return value;
        // Fallback: accept the C# name too (handy for tests).
        if (Enum.TryParse<TEnum>(s, ignoreCase: true, out var parsed)) return parsed;
        throw new JsonException($"unknown {typeof(TEnum).Name} value: {s}");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        foreach (var (v, name) in Map)
            if (EqualityComparer<TEnum>.Default.Equals(v, value))
            {
                writer.WriteStringValue(name);
                return;
            }
        writer.WriteStringValue(value.ToString().ToLowerInvariant());
    }
}

// Concrete subclasses so [JsonConverter(typeof(...))] can name them without open generics.
public sealed class ResourceTypeJsonConverter            : EnumMemberConverterBase<ResourceType> { }
public sealed class RequestTypeJsonConverter             : EnumMemberConverterBase<RequestType> { }
public sealed class QueryTypeJsonConverter               : EnumMemberConverterBase<QueryType> { }
public sealed class SortTypeJsonConverter                : EnumMemberConverterBase<SortType> { }
public sealed class TaskTypeJsonConverter                : EnumMemberConverterBase<TaskType> { }
public sealed class PublicSubmitResourceTypeJsonConverter: EnumMemberConverterBase<PublicSubmitResourceType> { }
public sealed class ContentTypeJsonConverter             : EnumMemberConverterBase<ContentType> { }
public sealed class UserTypeJsonConverter                : EnumMemberConverterBase<UserType> { }
public sealed class LanguageJsonConverter                : EnumMemberConverterBase<Language> { }
public sealed class ActionTypeJsonConverter             : EnumMemberConverterBase<ActionType> { }
public sealed class PluginTypeJsonConverter             : EnumMemberConverterBase<PluginType> { }
public sealed class EventListenTimeJsonConverter        : EnumMemberConverterBase<EventListenTime> { }
