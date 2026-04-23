// AOT-ready source-generated JSON for dmart.Client.
//
// Only compiled on net8.0+ where System.Text.Json's JsonSerializerContext
// exists. The netstandard2.1 leg continues to use reflection-based
// JsonSerializerOptions (DefaultJsonOptions) — that leg is not AOT-safe,
// but .NET runtimes that support AOT (net6+) don't use the netstandard
// path anyway, so this split gives modern consumers trim-safe plumbing
// without breaking older ones.
//
// The 5 reflection callsites at DmartClient.cs:102,237,256 and
// DmartClient.Extra.cs:227,278 route through this context on net8.0+.
// Wire convention matches the server (SnakeCaseLower, omit-when-null).
#if NET8_0_OR_GREATER

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Dmart.Models.Api;
using Dmart.Models.Enums;

namespace Dmart.Client.Json;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DictionaryKeyPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Response))]
[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(Record))]
[JsonSerializable(typeof(Query))]
[JsonSerializable(typeof(Error))]
[JsonSerializable(typeof(ResourceType))]
// Dictionary<string, object?> — used for ad-hoc request bodies (login, otp,
// reset, etc.). Nullability annotations are erased at runtime, so the
// canonical typeof() form is Dictionary<string, object>.
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
internal partial class DmartClientJsonContext : JsonSerializerContext;

#endif
