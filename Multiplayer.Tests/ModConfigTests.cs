using Multiplayer.Core.Build;
using Multiplayer.Core.Protocol;
using Xunit;

namespace Multiplayer.Tests
{
    /// <summary>Another mod's runtime configuration (a Road Builder road) on the wire.</summary>
    public class ModConfigTests
    {
        [Fact]
        public void RoundTrips()
        {
            var command = new ModConfigCommand { Id = "r82bf1c03-f638-4a0a-abf4-582b3869f4d8-76561198221749684", Name = "Custom Four-Lane Road", Json = "{\"Type\":\"RoadConfig\",\"Version\":5,\"Lanes\":[{\"GroupPrefabName\":\"RoadBuilder.Car\"}]}" };
            ModConfigCommand copy = ModConfigCommand.FromBytes(command.ToBytes());
            Assert.Equal(command.Id, copy.Id);
            Assert.Equal(command.Name, copy.Name);
            Assert.Equal(command.Json, copy.Json);
            Assert.Contains("Custom Four-Lane Road", copy.ToString());
        }

        [Fact]
        public void RefusesGarbage()
        {
            Assert.Throws<ProtocolException>(() => ModConfigCommand.FromBytes(new byte[] { 9, 1, 2 }));
        }
    }
}
