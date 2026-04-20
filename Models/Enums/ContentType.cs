using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Dmart.Models.Json;

namespace Dmart.Models.Enums;

[JsonConverter(typeof(ContentTypeJsonConverter))]
// Mirrors dmart/backend/models/enums.py::ContentType. Note: image is split per-format
// (no generic "image"); the dmart Python class has _missing_("image") → image_jpeg.
public enum ContentType
{
    [EnumMember(Value = "text")]        Text,
    [EnumMember(Value = "comment")]     Comment,
    [EnumMember(Value = "reaction")]    Reaction,
    [EnumMember(Value = "markdown")]    Markdown,
    [EnumMember(Value = "html")]        Html,
    [EnumMember(Value = "json")]        Json,
    [EnumMember(Value = "image")]       Image,
    [EnumMember(Value = "image_jpeg")]  ImageJpeg,
    [EnumMember(Value = "image_png")]   ImagePng,
    [EnumMember(Value = "image_svg")]   ImageSvg,
    [EnumMember(Value = "image_gif")]   ImageGif,
    [EnumMember(Value = "image_webp")]  ImageWebp,
    [EnumMember(Value = "python")]      Python,
    [EnumMember(Value = "pdf")]         Pdf,
    [EnumMember(Value = "audio")]       Audio,
    [EnumMember(Value = "video")]       Video,
    [EnumMember(Value = "csv")]         Csv,
    [EnumMember(Value = "parquet")]     Parquet,
    [EnumMember(Value = "jsonl")]       Jsonl,
    [EnumMember(Value = "apk")]         Apk,
    [EnumMember(Value = "sqlite")]      Sqlite,
}
