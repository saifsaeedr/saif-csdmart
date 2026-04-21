using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Dmart.Models.Json;

namespace Dmart.Models.Enums;

// Mirrors dmart/backend/models/enums.py::ActionType. These are the action kinds
// that can fire plugin hooks (before/after). Values are the exact strings used
// on the wire in config.json filter blocks.
[JsonConverter(typeof(ActionTypeJsonConverter))]
public enum ActionType
{
    [EnumMember(Value = "query")]            Query,
    [EnumMember(Value = "view")]             View,
    [EnumMember(Value = "update")]           Update,
    [EnumMember(Value = "create")]           Create,
    [EnumMember(Value = "delete")]           Delete,
    [EnumMember(Value = "attach")]           Attach,
    [EnumMember(Value = "assign")]           Assign,
    [EnumMember(Value = "move")]             Move,
    [EnumMember(Value = "progress_ticket")]  ProgressTicket,
    [EnumMember(Value = "lock")]             Lock,
    [EnumMember(Value = "unlock")]           Unlock,
}
