using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Dmart.Models.Json;

namespace Dmart.Models.Enums;

// Mirrors dmart/backend/models/enums.py::QueryType.
[JsonConverter(typeof(QueryTypeJsonConverter))]
public enum QueryType
{
    [EnumMember(Value = "search")]                   Search,
    [EnumMember(Value = "subpath")]                  Subpath,
    [EnumMember(Value = "events")]                   Events,
    [EnumMember(Value = "history")]                  History,
    [EnumMember(Value = "tags")]                     Tags,
    [EnumMember(Value = "random")]                   Random,
    [EnumMember(Value = "spaces")]                   Spaces,
    [EnumMember(Value = "counters")]                 Counters,
    [EnumMember(Value = "reports")]                  Reports,
    [EnumMember(Value = "aggregation")]              Aggregation,
    [EnumMember(Value = "attachments")]              Attachments,
    [EnumMember(Value = "attachments_aggregation")]  AttachmentsAggregation,
}
