using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Dmart.Models.Json;

namespace Dmart.Models.Enums;

[JsonConverter(typeof(UserTypeJsonConverter))]
// dmart stores this as a PostgreSQL ENUM type "usertype" — values must match exactly.
public enum UserType
{
    [EnumMember(Value = "web")]     Web,
    [EnumMember(Value = "mobile")]  Mobile,
    [EnumMember(Value = "bot")]     Bot,
}
