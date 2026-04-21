using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Dmart.Models.Json;

namespace Dmart.Models.Enums;

[JsonConverter(typeof(TaskTypeJsonConverter))]
public enum TaskType
{
    [EnumMember(Value = "query")]  Query,
}
