using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Dmart.Models.Json;

namespace Dmart.Models.Enums;

[JsonConverter(typeof(JoinTypeJsonConverter))]
public enum JoinType
{
    [EnumMember(Value = "left")]   Left,
    [EnumMember(Value = "right")]  Right,
    [EnumMember(Value = "inner")]  Inner,
    [EnumMember(Value = "outer")]  Outer,
}
