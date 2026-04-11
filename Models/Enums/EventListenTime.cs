using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Dmart.Models.Json;

namespace Dmart.Models.Enums;

// Mirrors dmart/backend/models/enums.py::EventListenTime. Declares whether a
// hook plugin should run before or after the action's DB side effects.
[JsonConverter(typeof(EventListenTimeJsonConverter))]
public enum EventListenTime
{
    [EnumMember(Value = "before")] Before,
    [EnumMember(Value = "after")]  After,
}
