using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Dmart.Models.Json;

namespace Dmart.Models.Enums;

// Mirrors dmart/backend/models/enums.py::ResourceType. The string values stored in
// the resource_type column MUST match the Python enum values exactly.
[JsonConverter(typeof(ResourceTypeJsonConverter))]
public enum ResourceType
{
    [EnumMember(Value = "user")]            User,
    [EnumMember(Value = "group")]           Group,
    [EnumMember(Value = "folder")]          Folder,
    [EnumMember(Value = "schema")]          Schema,
    [EnumMember(Value = "content")]         Content,
    [EnumMember(Value = "log")]             Log,
    [EnumMember(Value = "acl")]             Acl,
    [EnumMember(Value = "comment")]         Comment,
    [EnumMember(Value = "media")]           Media,
    [EnumMember(Value = "data_asset")]      DataAsset,
    [EnumMember(Value = "locator")]         Locator,
    [EnumMember(Value = "relationship")]    Relationship,
    [EnumMember(Value = "alteration")]      Alteration,
    [EnumMember(Value = "history")]         History,
    [EnumMember(Value = "space")]           Space,
    [EnumMember(Value = "permission")]      Permission,
    [EnumMember(Value = "role")]            Role,
    [EnumMember(Value = "ticket")]          Ticket,
    [EnumMember(Value = "json")]            Json,
    [EnumMember(Value = "lock")]            Lock,
    [EnumMember(Value = "post")]            Post,
    [EnumMember(Value = "reaction")]        Reaction,
    [EnumMember(Value = "reply")]           Reply,
    [EnumMember(Value = "share")]           Share,
    [EnumMember(Value = "plugin_wrapper")]  PluginWrapper,
    [EnumMember(Value = "notification")]    Notification,
    [EnumMember(Value = "csv")]             Csv,
    [EnumMember(Value = "jsonl")]           Jsonl,
    [EnumMember(Value = "sqlite")]          Sqlite,
    [EnumMember(Value = "parquet")]         Parquet,
}
