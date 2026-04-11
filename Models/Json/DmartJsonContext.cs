using System.Text.Json;
using System.Text.Json.Serialization;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Plugins.BuiltIn;

namespace Dmart.Models.Json;

// AOT-friendly source-generated JSON. Every type that crosses the wire (or jsonb)
// MUST be listed here. JsonStringEnumConverter combined with [EnumMember(Value=...)]
// on the enum members produces the exact strings dmart's Python uses.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
// Enums use per-type [JsonConverter] attributes that read [EnumMember] — see
// EnumMemberConverter.cs. We deliberately don't set UseStringEnumConverter here
// because that emits source-gen converters that ignore [EnumMember].
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
[JsonSerializable(typeof(RealtimeBroadcastBody))]
[JsonSerializable(typeof(RealtimeMessageBody))]
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
[JsonSerializable(typeof(Dictionary<string, List<string>>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public partial class DmartJsonContext : JsonSerializerContext;
