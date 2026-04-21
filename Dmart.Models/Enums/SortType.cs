using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Dmart.Models.Json;

namespace Dmart.Models.Enums;

[JsonConverter(typeof(SortTypeJsonConverter))]
public enum SortType
{
    [EnumMember(Value = "ascending")]   Ascending,
    [EnumMember(Value = "descending")]  Descending,
}
