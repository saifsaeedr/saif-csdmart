using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Dmart.Models.Json;

namespace Dmart.Models.Enums;

// dmart only allows two resource types via /public/submit.
[JsonConverter(typeof(PublicSubmitResourceTypeJsonConverter))]
public enum PublicSubmitResourceType
{
    [EnumMember(Value = "content")]  Content,
    [EnumMember(Value = "ticket")]   Ticket,
}
