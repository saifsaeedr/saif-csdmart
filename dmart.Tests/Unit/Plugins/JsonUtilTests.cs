using System.Text;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Plugins;

// Covers JsonUtil.Compact — the helper that round-trips a JSON buffer
// through JsonDocument with Indented=false so multi-line plugin
// config.json contents fit on a single log line.
public class JsonUtilTests
{
    [Fact]
    public void Compact_Strips_Whitespace_From_Indented_Json()
    {
        var indented = """
            {
              "shortname": "hi",
              "is_active": true,
              "type": "hook"
            }
            """;
        JsonUtil.Compact(indented).ShouldBe("{\"shortname\":\"hi\",\"is_active\":true,\"type\":\"hook\"}");
    }

    [Fact]
    public void Compact_Bytes_Overload_Matches_String_Overload()
    {
        var indented = "{\n  \"a\": 1,\n  \"b\": [1, 2]\n}";
        var fromBytes = JsonUtil.Compact(Encoding.UTF8.GetBytes(indented));
        var fromString = JsonUtil.Compact(indented);
        fromBytes.ShouldBe(fromString);
        fromBytes.ShouldBe("{\"a\":1,\"b\":[1,2]}");
    }

    [Fact]
    public void Compact_Preserves_Unicode_Characters()
    {
        // UnsafeRelaxedJsonEscaping is the default for JsonDocument.WriteTo
        // path used here; non-ASCII characters survive without being
        // \uXXXX-escaped.
        var input = "{\"msg\": \"مرحبا\"}";
        JsonUtil.Compact(input).ShouldBe("{\"msg\":\"مرحبا\"}");
    }

    [Fact]
    public void Compact_Falls_Back_To_Raw_String_On_Malformed_Json()
    {
        const string broken = "{not valid json";
        JsonUtil.Compact(broken).ShouldBe(broken);
        JsonUtil.Compact(Encoding.UTF8.GetBytes(broken))
            .ShouldBe(broken);
    }

    [Fact]
    public void Compact_Handles_Empty_Object_And_Array()
    {
        JsonUtil.Compact("{ }").ShouldBe("{}");
        JsonUtil.Compact("[\n  \n]").ShouldBe("[]");
    }
}
