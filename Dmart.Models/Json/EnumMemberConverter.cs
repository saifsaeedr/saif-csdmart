using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dmart.Models.Api;
using Dmart.Models.Enums;

namespace Dmart.Models.Json;

// Source-gen JSON in .NET 10 doesn't honor [EnumMember] or apply PropertyNamingPolicy
// to enum values, so we provide explicit per-enum converters that read [EnumMember]
// at first use and then look up via cached arrays. AOT-safe: each typed converter
// is concrete, no open generics emitted to attributes. The [DynamicallyAccessedMembers]
// annotation on TEnum keeps the enum's public fields alive under trim so
// `typeof(TEnum).GetField(...)` at line 32 resolves.
public abstract class EnumMemberConverterBase<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum>
    : JsonConverter<TEnum> where TEnum : struct, Enum
{
    private static readonly (TEnum Value, string Name)[] Map = BuildMap();

    // Wire-format names in declaration order. Exposed so the dmart server's
    // OpenAPI generator can inject the proper string-enum schema for this
    // type — the [JsonConverter] attribute on TEnum hides it from .NET's
    // built-in schema introspection, which would otherwise emit a $ref
    // without a matching component.
    public static IReadOnlyList<string> WireValues { get; } = Map.Select(x => x.Name).ToArray();

    private static (TEnum, string)[] BuildMap()
    {
#if NET5_0_OR_GREATER
        // Generic overload is AOT-friendly (no dynamic code required). .NET 10
        // AOT publish warns on the non-generic version with IL3050.
        var values = Enum.GetValues<TEnum>();
#else
        // netstandard2.1 predates Enum.GetValues<TEnum>(); fall back to the
        // non-generic overload and cast the weakly-typed Array to TEnum[].
        var values = (TEnum[])Enum.GetValues(typeof(TEnum));
#endif
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
public sealed class JoinTypeJsonConverter                : EnumMemberConverterBase<JoinType> { }
public sealed class TaskTypeJsonConverter                : EnumMemberConverterBase<TaskType> { }
public sealed class PublicSubmitResourceTypeJsonConverter: EnumMemberConverterBase<PublicSubmitResourceType> { }
public sealed class ContentTypeJsonConverter             : EnumMemberConverterBase<ContentType> { }
public sealed class UserTypeJsonConverter                : EnumMemberConverterBase<UserType> { }
public sealed class LanguageJsonConverter                : EnumMemberConverterBase<Language> { }
public sealed class ActionTypeJsonConverter             : EnumMemberConverterBase<ActionType> { }
public sealed class PluginTypeJsonConverter             : EnumMemberConverterBase<PluginType> { }
public sealed class EventListenTimeJsonConverter        : EnumMemberConverterBase<EventListenTime> { }

// Index of every enum whose JSON wire format is governed by an
// EnumMemberConverterBase<T> above. The dmart server walks this to inject
// the missing string-enum schemas into the generated OpenAPI document.
// Adding a new concrete converter above MUST also add an entry here, or
// Swagger UI will report an unresolved $ref for the new enum.
public static class EnumMemberConverters
{
    public static readonly (string SchemaName, IReadOnlyList<string> WireValues)[] All =
    {
        (nameof(Status),                   StatusJsonConverter.WireValues),
        (nameof(ResourceType),             ResourceTypeJsonConverter.WireValues),
        (nameof(RequestType),              RequestTypeJsonConverter.WireValues),
        (nameof(QueryType),                QueryTypeJsonConverter.WireValues),
        (nameof(SortType),                 SortTypeJsonConverter.WireValues),
        (nameof(JoinType),                 JoinTypeJsonConverter.WireValues),
        (nameof(TaskType),                 TaskTypeJsonConverter.WireValues),
        (nameof(PublicSubmitResourceType), PublicSubmitResourceTypeJsonConverter.WireValues),
        (nameof(ContentType),              ContentTypeJsonConverter.WireValues),
        (nameof(UserType),                 UserTypeJsonConverter.WireValues),
        (nameof(Language),                 LanguageJsonConverter.WireValues),
        (nameof(ActionType),               ActionTypeJsonConverter.WireValues),
        (nameof(PluginType),               PluginTypeJsonConverter.WireValues),
        (nameof(EventListenTime),          EventListenTimeJsonConverter.WireValues),
    };
}
