using System.Collections.Generic;
using Multiplayer.Core.Build;
using Multiplayer.Core.Protocol;
using Xunit;

namespace Multiplayer.Tests
{
    public class BuildCommandTests
    {
        private static BuildCommand Sample()
        {
            return new BuildCommand
            {
                Sequence = 7,
                ToolId = "Net Tool",
                Definitions =
                {
                    new DefinitionData
                    {
                        Prefab = new PrefabKey { Type = "RoadPrefab", Name = "Small Road" },
                        Flags = 0x1,
                        RandomSeed = 12345,
                        Course = new NetCourseData
                        {
                            Start = new CoursePosData { Position = new Vec3(10, 50, 20), Flags = 1, CourseDelta = 0, ParentMesh = -1 },
                            End = new CoursePosData
                            {
                                Entity = new EntityRef { Kind = EntityKind.NetNode, Prefab = new PrefabKey { Type = "RoadPrefab", Name = "Small Road" }, Position = new Vec3(90, 50, 20) },
                                Position = new Vec3(90, 50, 20),
                                Flags = 2,
                                CourseDelta = 1,
                                ParentMesh = -1,
                            },
                            A = new Vec3(10, 50, 20),
                            B = new Vec3(36.6f, 50, 20),
                            C = new Vec3(63.3f, 50, 20),
                            D = new Vec3(90, 50, 20),
                            Length = 80,
                            FixedIndex = -1,
                        },
                    },
                    new DefinitionData
                    {
                        Prefab = new PrefabKey { Type = "BuildingPrefab", Name = "Fire House" },
                        Owner = new EntityRef { Kind = EntityKind.Object, Prefab = new PrefabKey { Type = "BuildingPrefab", Name = "Fire House" }, Position = new Vec3(1, 2, 3) },
                        Object = new ObjectDefinitionData { Position = new Vec3(5, 6, 7), Scale = new Vec3(1, 1, 1), Elevation = 2.5f, Probability = 100, PrefabSubIndex = -1 },
                        OwnerDefinition = new OwnerDefinitionData { Prefab = new PrefabKey { Type = "BuildingPrefab", Name = "Fire House" }, Position = new Vec3(5, 6, 7) },
                        AreaNodes = new List<AreaNodeData> { new AreaNodeData { Position = new Vec3(0, 0, 0), Elevation = 1 }, new AreaNodeData { Position = new Vec3(10, 0, 0), Elevation = 1 } },
                    },
                    new DefinitionData
                    {
                        Original = new EntityRef { Kind = EntityKind.NetEdge, Prefab = new PrefabKey { Type = "RoadPrefab", Name = "Alley" }, Position = new Vec3(3, 3, 3), Aux = new Vec3(0, 3, 3) },
                        Flags = 4,
                    },
                },
            };
        }

        [Fact]
        public void RoundTrips_AllParts()
        {
            BuildCommand original = Sample();
            BuildCommand decoded = BuildCommand.FromBytes(original.ToBytes());

            Assert.Equal(7, decoded.Sequence);
            Assert.Equal("Net Tool", decoded.ToolId);
            Assert.Equal(3, decoded.Definitions.Count);

            DefinitionData road = decoded.Definitions[0];
            Assert.Equal("Small Road", road.Prefab.Name);
            Assert.NotNull(road.Course);
            Assert.Null(road.Object);
            Assert.Equal(EntityKind.NetNode, road.Course.End.Entity.Kind);
            Assert.Equal(90f, road.Course.End.Entity.Position.X);
            Assert.Equal(80f, road.Course.Length);
            Assert.Equal(-1, road.Course.FixedIndex);
            Assert.Equal(12345, road.RandomSeed);

            DefinitionData building = decoded.Definitions[1];
            Assert.NotNull(building.Object);
            Assert.NotNull(building.OwnerDefinition);
            Assert.Equal(2, building.AreaNodes.Count);
            Assert.Equal(2.5f, building.Object.Elevation);
            Assert.Equal(EntityKind.Object, building.Owner.Kind);

            DefinitionData delete = decoded.Definitions[2];
            Assert.Equal(EntityKind.NetEdge, delete.Original.Kind);
            Assert.Equal(0f, delete.Original.Aux.X);
            Assert.Equal(4u, delete.Flags);
            Assert.Null(delete.Course);
        }

        [Fact]
        public void EmptyAndMalformed()
        {
            BuildCommand empty = BuildCommand.FromBytes(new BuildCommand { Sequence = 1, ToolId = "Bulldoze Tool" }.ToBytes());
            Assert.Empty(empty.Definitions);

            byte[] bytes = Sample().ToBytes();
            var truncated = new byte[bytes.Length / 2];
            System.Array.Copy(bytes, truncated, truncated.Length);
            Assert.Throws<ProtocolException>(() => BuildCommand.FromBytes(truncated));
            Assert.Throws<ProtocolException>(() => BuildCommand.FromBytes(new byte[] { 99 }));
        }

        [Fact]
        public void TravelsInsideAGameplayCommand()
        {
            var message = new GameplayCommandMessage { Kind = BuildCommand.Kind, OriginPlayerId = 3, Payload = Sample().ToBytes() };
            var decoded = (GameplayCommandMessage)MessageCodec.Decode(MessageCodec.Encode(message));
            Assert.Equal(BuildCommand.Kind, decoded.Kind);
            BuildCommand command = BuildCommand.FromBytes(decoded.Payload);
            Assert.Equal(3, decoded.OriginPlayerId);
            Assert.Equal(3, command.Definitions.Count);
        }

        [Fact]
        public void BrushStrokeRoundTrips()
        {
            var command = new BuildCommand
            {
                Sequence = 3,
                ToolId = "Terrain Tool",
                Definitions =
                {
                    new DefinitionData
                    {
                        Prefab = new PrefabKey { Type = "BrushPrefab", Name = "Circle" },
                        Brush = new BrushDefinitionData
                        {
                            Tool = new PrefabKey { Type = "TerraformingPrefab", Name = "Raise" },
                            LineA = new Vec3(1, 2, 3),
                            LineB = new Vec3(4, 5, 6),
                            Target = new Vec3(7, 8, 9),
                            Start = new Vec3(10, 11, 12),
                            Angle = 0.5f,
                            Size = 60f,
                            Strength = 0.75f,
                            Time = 0.016f,
                        },
                    },
                },
            };

            BuildCommand copy = BuildCommand.FromBytes(command.ToBytes());

            DefinitionData definition = Assert.Single(copy.Definitions);
            Assert.NotNull(definition.Brush);
            Assert.Null(definition.Course);
            Assert.Equal("Raise", definition.Brush.Tool.Name);
            Assert.Equal(4f, definition.Brush.LineB.X);
            Assert.Equal(9f, definition.Brush.Target.Z);
            Assert.Equal(11f, definition.Brush.Start.Y);
            Assert.Equal(0.5f, definition.Brush.Angle);
            Assert.Equal(60f, definition.Brush.Size);
            Assert.Equal(0.75f, definition.Brush.Strength);
            Assert.Equal(0.016f, definition.Brush.Time);
            Assert.Contains("brush", definition.ToString());
        }


        [Fact]
        public void TransitLineRoundTrips()
        {
            var command = new BuildCommand
            {
                Sequence = 4,
                ToolId = "Route Tool",
                Definitions =
                {
                    new DefinitionData
                    {
                        Prefab = new PrefabKey { Type = "TransportLinePrefab", Name = "Bus Line" },
                        Original = new EntityRef { Kind = EntityKind.Route, Prefab = new PrefabKey { Type = "TransportLinePrefab", Name = "Bus Line" }, Aux = new Vec3(7, 0, 0) },
                        Color = new ColorData { R = 10, G = 20, B = 30, A = 255 },
                        Waypoints = new List<WaypointData>
                        {
                            new WaypointData
                            {
                                Position = new Vec3(1, 2, 3),
                                Connection = new EntityRef { Kind = EntityKind.Object, Prefab = new PrefabKey { Type = "TransportStopPrefab", Name = "Bus Stop" }, Position = new Vec3(1, 2, 3) },
                                Original = new EntityRef { Kind = EntityKind.Waypoint, Position = new Vec3(1, 2, 3) },
                            },
                            new WaypointData { Position = new Vec3(4, 5, 6) },
                        },
                    },
                },
            };

            BuildCommand copy = BuildCommand.FromBytes(command.ToBytes());

            DefinitionData definition = Assert.Single(copy.Definitions);
            Assert.Equal(EntityKind.Route, definition.Original.Kind);
            Assert.Equal(7f, definition.Original.Aux.X);
            Assert.Contains("#7", definition.Original.ToString());
            Assert.NotNull(definition.Color);
            Assert.Equal(30, definition.Color.B);
            Assert.NotNull(definition.Waypoints);
            Assert.Equal(2, definition.Waypoints.Count);
            Assert.Equal("Bus Stop", definition.Waypoints[0].Connection.Prefab.Name);
            Assert.Equal(EntityKind.Waypoint, definition.Waypoints[0].Original.Kind);
            Assert.Equal(EntityKind.None, definition.Waypoints[1].Connection.Kind);
            Assert.Equal(6f, definition.Waypoints[1].Position.Z);
            Assert.Contains("line", definition.ToString());
        }

    }
}
