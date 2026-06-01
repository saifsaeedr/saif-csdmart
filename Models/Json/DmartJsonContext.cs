using System.Text.Json;
using System.Text.Json.Serialization;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Plugins.Native;
using Dmart.Services;

namespace Dmart.Models.Json;

// AOT-friendly source-generated JSON. Every type that crosses the wire (or jsonb)
// MUST be listed here. JsonStringEnumConverter combined with [EnumMember(Value=...)]
// on the enum members produces the exact strings dmart's Python uses.
// MaxDepth caps JSON nesting at 32 levels — protects against "JSON bomb" DoS
// (deeply nested payloads that exhaust the stack). 32 is well above anything
// dmart legitimately sends/receives: the deepest legitimate shape is a
// Request envelope carrying Records with a Payload.Body that nests maybe
// 4-5 levels.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    MaxDepth = 32,
    // Local-naive DateTime wire format mirrors Python pydantic naive
    // datetimes (no offset, server-local wall clock). DateTimeOffset is
    // unaffected — it serializes with its own carried offset.
    Converters = new[] { typeof(LocalNaiveDateTimeConverter) })]
// Enums use per-type [JsonConverter] attributes that read [EnumMember] — see
// EnumMemberConverter.cs. We deliberately don't set UseStringEnumConverter here
// because that emits source-gen converters that ignore [EnumMember].
[JsonSerializable(typeof(ImportCheckpointStore))]
[JsonSerializable(typeof(Query))]
[JsonSerializable(typeof(JoinQuery))]
[JsonSerializable(typeof(RedisAggregate))]
[JsonSerializable(typeof(RedisReducer))]
[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(Record))]
[JsonSerializable(typeof(Response))]
[JsonSerializable(typeof(Error))]
[JsonSerializable(typeof(Status))]
[JsonSerializable(typeof(UserLoginRequest))]
[JsonSerializable(typeof(SendOTPRequest))]
[JsonSerializable(typeof(ConfirmOTPRequest))]
[JsonSerializable(typeof(PasswordResetRequest))]
[JsonSerializable(typeof(PasswordResetConfirm))]
[JsonSerializable(typeof(UserCreateBody))]
[JsonSerializable(typeof(RegisterRequest))]
[JsonSerializable(typeof(RegisterResponse))]
// Documentation-only request shapes (see Dmart.Models/Api/DocsDtos.cs).
// The endpoints that accept these parse the body manually — the types
// exist so the OpenAPI generator can emit a stable schema, and the
// source-gen resolver (used as the default TypeInfoResolver in
// Program.cs) needs to know them.
[JsonSerializable(typeof(ValidatePasswordBody))]
[JsonSerializable(typeof(ProfileUpdateBody))]
[JsonSerializable(typeof(SemanticSearchBody))]
[JsonSerializable(typeof(ReindexEmbeddingsBody))]
[JsonSerializable(typeof(ExecuteTaskBody))]
[JsonSerializable(typeof(ProgressTicketBody))]
[JsonSerializable(typeof(OAuthMobileLoginBody))]
[JsonSerializable(typeof(WsSendMessageBody))]
[JsonSerializable(typeof(WsBroadcastBody))]
[JsonSerializable(typeof(McpRequestBody))]
[JsonSerializable(typeof(HttpValidationError))]
[JsonSerializable(typeof(ValidationError))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(Locator))]
[JsonSerializable(typeof(Translation))]
[JsonSerializable(typeof(Payload))]
[JsonSerializable(typeof(Reporter))]
[JsonSerializable(typeof(AclEntry))]
[JsonSerializable(typeof(Entry))]
[JsonSerializable(typeof(Attachment))]
[JsonSerializable(typeof(User))]
[JsonSerializable(typeof(Role))]
[JsonSerializable(typeof(Permission))]
[JsonSerializable(typeof(Space))]
[JsonSerializable(typeof(ResourceType))]
[JsonSerializable(typeof(RequestType))]
[JsonSerializable(typeof(QueryType))]
[JsonSerializable(typeof(SortType))]
[JsonSerializable(typeof(JoinType))]
[JsonSerializable(typeof(TaskType))]
[JsonSerializable(typeof(PublicSubmitResourceType))]
[JsonSerializable(typeof(ContentType))]
[JsonSerializable(typeof(UserType))]
[JsonSerializable(typeof(Language))]
[JsonSerializable(typeof(ActionType))]
[JsonSerializable(typeof(PluginType))]
[JsonSerializable(typeof(EventListenTime))]
[JsonSerializable(typeof(PluginWrapper))]
[JsonSerializable(typeof(EventFilter))]
[JsonSerializable(typeof(List<Record>))]
[JsonSerializable(typeof(List<Entry>))]
[JsonSerializable(typeof(List<User>))]
[JsonSerializable(typeof(List<Permission>))]
[JsonSerializable(typeof(List<Role>))]
[JsonSerializable(typeof(List<Attachment>))]
[JsonSerializable(typeof(List<AclEntry>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<Language>))]
[JsonSerializable(typeof(List<Dictionary<string, object>>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
// Scalar types that appear inside Dictionary<string, object> attribute bags.
// Without explicit registration, the source-gen serializer throws at runtime
// for any ValueType it hasn't been told about — seen on semantic_search's
// similarity score (double), the access log's Environment.ProcessId (int),
// and JSON payload-body fields that parse as Int64/bool under
// Dictionary<string, object> (the access log serializes the request body
// through the same source-gen path).
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(Dictionary<string, List<string>>))]
[JsonSerializable(typeof(Dictionary<string, List<Record>>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Event))]
[JsonSerializable(typeof(NativeApiRequest))]
public partial class DmartJsonContext : JsonSerializerContext;
