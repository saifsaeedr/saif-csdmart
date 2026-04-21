using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Dmart.Models.Json;

namespace Dmart.Models.Enums;

// Mirrors dmart/backend/models/enums.py::RequestType.
[JsonConverter(typeof(RequestTypeJsonConverter))]
public enum RequestType
{
    [EnumMember(Value = "create")]      Create,
    [EnumMember(Value = "update")]      Update,
    [EnumMember(Value = "patch")]       Patch,
    [EnumMember(Value = "update_acl")]  UpdateAcl,
    [EnumMember(Value = "assign")]      Assign,
    [EnumMember(Value = "delete")]      Delete,
    [EnumMember(Value = "move")]        Move,
}
