using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Dmart.Models.Json;

namespace Dmart.Models.Enums;

// Mirrors dmart/backend/models/enums.py::PluginType. A "hook" plugin gets
// invoked on before/after action events; an "api" plugin mounts its own
// HTTP router at /{shortname}/.
[JsonConverter(typeof(PluginTypeJsonConverter))]
public enum PluginType
{
    [EnumMember(Value = "hook")] Hook,
    [EnumMember(Value = "api")]  Api,
}
