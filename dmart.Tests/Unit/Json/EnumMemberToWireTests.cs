using Dmart.Models.Enums;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Json;

public sealed class EnumMemberToWireTests
{
    [Fact]
    public void ToWire_Returns_EnumMember_Value_For_ResourceType()
    {
        ResourceTypeJsonConverter.ToWire(ResourceType.Content).ShouldBe("content");
        ResourceTypeJsonConverter.ToWire(ResourceType.Ticket).ShouldBe("ticket");
        ResourceTypeJsonConverter.ToWire(ResourceType.Folder).ShouldBe("folder");
        ResourceTypeJsonConverter.ToWire(ResourceType.DataAsset).ShouldBe("data_asset");
    }
}
