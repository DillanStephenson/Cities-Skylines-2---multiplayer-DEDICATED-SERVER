using Multiplayer.Core.Build;
using Xunit;

namespace Multiplayer.Tests
{
    /// <summary>The Traffic mod's lane connections on the wire: groups per lane, links per group, all edges named by position.</summary>
    public class LaneConnectionsTests
    {
        [Fact]
        public void RoundTrips()
        {
            var command = new LaneConnectionsCommand { Node = new EntityRef { Kind = EntityKind.NetNode, Prefab = new PrefabKey { Type = "RoadPrefab", Name = "Medium Road" }, Position = new Vec3(10, 20, 30) } };
            var group = new LaneGroupData { LaneIndex = 2, CarriagewayX = 1, CarriagewayY = 0, LanePosition = new Vec3(1, 2, 3), Edge = new EntityRef { Kind = EntityKind.NetEdge, Position = new Vec3(5, 6, 7), Aux = new Vec3(8, 9, 10) } };
            var link = new LaneConnectionData
            {
                Source = new EntityRef { Kind = EntityKind.NetEdge, Position = new Vec3(5, 6, 7), Aux = new Vec3(8, 9, 10) },
                Target = new EntityRef { Kind = EntityKind.NetEdge, Position = new Vec3(15, 16, 17), Aux = new Vec3(18, 19, 20) },
                LaneIndexX = 2,
                LaneIndexY = 3,
                GroupMap = new[] { 1, 0, 2, 1 },
                PositionA = new Vec3(0.5f, 0, 1),
                PositionB = new Vec3(2, 0, 3.5f),
                Method = 6,
                IsUnsafe = true,
            };
            group.Connections.Add(link);
            command.Groups.Add(group);

            LaneConnectionsCommand copy = LaneConnectionsCommand.FromBytes(command.ToBytes());

            Assert.Equal("Medium Road", copy.Node.Prefab.Name);
            Assert.False(copy.Remove);
            Assert.Single(copy.Groups);
            Assert.Equal(2, copy.Groups[0].LaneIndex);
            Assert.Equal(1, copy.Groups[0].CarriagewayX);
            Assert.Equal(3f, copy.Groups[0].LanePosition.Z);
            Assert.Equal(10f, copy.Groups[0].Edge.Aux.Z);
            LaneConnectionData copyLink = copy.Groups[0].Connections[0];
            Assert.Equal(17f, copyLink.Target.Position.Z);
            Assert.Equal(3, copyLink.LaneIndexY);
            Assert.Equal(new[] { 1, 0, 2, 1 }, copyLink.GroupMap);
            Assert.Equal(3.5f, copyLink.PositionB.Z);
            Assert.Equal(6, copyLink.Method);
            Assert.True(copyLink.IsUnsafe);
            Assert.Contains("1 lanes, 1 links", copy.ToString());
        }

        [Fact]
        public void Removal_IsJustTheNode()
        {
            var command = new LaneConnectionsCommand { Node = new EntityRef { Kind = EntityKind.NetNode, Position = new Vec3(1, 2, 3) }, Remove = true };
            LaneConnectionsCommand copy = LaneConnectionsCommand.FromBytes(command.ToBytes());
            Assert.True(copy.Remove);
            Assert.Empty(copy.Groups);
            Assert.StartsWith("remove lane connections", copy.ToString());
        }
    }
}
