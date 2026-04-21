using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Dmart.Models.Json;

namespace Dmart.Models.Enums;

[JsonConverter(typeof(LanguageJsonConverter))]
// dmart stores the FULL spelling, not the ISO code: "arabic" not "ar". Stored as a
// PostgreSQL ENUM type "language".
public enum Language
{
    [EnumMember(Value = "arabic")]   Ar,
    [EnumMember(Value = "english")]  En,
    [EnumMember(Value = "kurdish")]  Ku,
    [EnumMember(Value = "french")]   Fr,
    [EnumMember(Value = "turkish")]  Tr,
}
