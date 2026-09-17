using Multiplayer.Core.Build;
using Multiplayer.Core.Protocol;
using Multiplayer.Core.Session;
using Xunit;

namespace Multiplayer.Tests
{
    /// <summary>Other mods' per-entity settings on the wire (raw struct bytes plus translated entity references), and replay results.</summary>
    public class ModDataTests
    {
        [Fact]
        public void ModData_RoundTrips()
        {
            var command = new ModDataCommand
            {
                Target = new EntityRef { Kind = EntityKind.NetNode, Prefab = new PrefabKey { Type = "RoadPrefab", Name = "Small Road" }, Position = new Vec3(10, 20, 30) },
                TypeName = "C2VM.TrafficToolEssentials.Components.EdgeGroupMask",
                IsBuffer = true,
                ElementSize = 16,
                Data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32 },
            };
            command.Entities.Add(new EntityPatch { Offset = 0, Ref = new EntityRef { Kind = EntityKind.NetEdge, Position = new Vec3(1, 2, 3), Aux = new Vec3(4, 5, 6) } });
            command.Entities.Add(new EntityPatch { Offset = 16, Ref = new EntityRef { Kind = EntityKind.Lane, Position = new Vec3(7, 8, 9), Aux = new Vec3(10, 11, 12) } });

            ModDataCommand copy = ModDataCommand.FromBytes(command.ToBytes());

            Assert.Equal(EntityKind.NetNode, copy.Target.Kind);
            Assert.Equal("Small Road", copy.Target.Prefab.Name);
            Assert.Equal(command.TypeName, copy.TypeName);
            Assert.True(copy.IsBuffer);
            Assert.False(copy.Remove);
            Assert.Equal(16, copy.ElementSize);
            Assert.Equal(command.Data, copy.Data);
            Assert.Equal(2, copy.Entities.Count);
            Assert.Equal(16, copy.Entities[1].Offset);
            Assert.Equal(EntityKind.Lane, copy.Entities[1].Ref.Kind);
            Assert.Equal(12f, copy.Entities[1].Ref.Aux.Z);
            Assert.Contains("EdgeGroupMask[2]", copy.ToString());
        }

        [Fact]
        public void ModData_RemovalCarriesNoBytes_AndModEntitiesKeepTheirIdentity()
        {
            var command = new ModDataCommand
            {
                Target = new EntityRef { Kind = EntityKind.ModEntity, Prefab = new PrefabKey { Type = "mod", Name = "C2VM.TrafficToolEssentials.Components.DepotZoneData" }, Position = new Vec3(500, 0, 600) },
                TypeName = "C2VM.TrafficToolEssentials.Components.DepotZoneData",
                Remove = true,
                ElementSize = 120,
            };

            ModDataCommand copy = ModDataCommand.FromBytes(command.ToBytes());
            Assert.True(copy.Remove);
            Assert.Empty(copy.Data);
            Assert.Equal(EntityKind.ModEntity, copy.Target.Kind);
            Assert.Equal("C2VM.TrafficToolEssentials.Components.DepotZoneData", copy.Target.Prefab.Name);
            Assert.Equal(600f, copy.Target.Position.Z);
            Assert.StartsWith("remove DepotZoneData", copy.ToString());
        }

        [Fact]
        public void ModData_RefusesAbsurdLengths()
        {
            byte[] bytes = new ModDataCommand { TypeName = "x", Data = new byte[4] }.ToBytes();
            // The data length sits right after the type name ("x" = length-prefixed) and the two flags and the element size.
            int lengthAt = 1 + 1 + (1 + 1) + 1 + 1 + 4;
            bytes[lengthAt] = 0xFF;
            bytes[lengthAt + 1] = 0xFF;
            bytes[lengthAt + 2] = 0xFF;
            bytes[lengthAt + 3] = 0x7F;
            Assert.Throws<ProtocolException>(() => ModDataCommand.FromBytes(bytes));
        }

        [Fact]
        public void BuildResult_RoundTrips_AndTrimsLongMessages()
        {
            var result = new BuildResultCommand { Sequence = 42, BuilderPlayerId = 3, ToolId = "Net Tool", Ok = false, Message = new string('x', 1000) };
            BuildResultCommand copy = BuildResultCommand.FromBytes(result.ToBytes());
            Assert.Equal(42, copy.Sequence);
            Assert.Equal(3, copy.BuilderPlayerId);
            Assert.Equal("Net Tool", copy.ToolId);
            Assert.False(copy.Ok);
            Assert.Equal(BuildResultCommand.MaxMessageLength, copy.Message.Length);
            Assert.Contains("build #42 (Net Tool) of player 3 failed", copy.ToString());
        }

        [Fact]
        public void Server_RelaysModDataAndResults_ToTheOthersOnly()
        {
            using var loop = new Loopback();
            loop.StartServer();
            ClientSession a = loop.StartOwner("Ada");
            ClientSession b = loop.StartClient("Bob");
            Assert.True(loop.PumpUntil(() => a.State == SessionState.Connected && b.State == SessionState.Connected));

            int aGot = 0;
            ModDataCommand bGot = null;
            BuildResultCommand aResult = null;
            a.GameplayCommandReceived += m =>
            {
                if (m.Kind == ModDataCommand.Kind)
                {
                    aGot++;
                }

                if (m.Kind == BuildResultCommand.Kind)
                {
                    aResult = BuildResultCommand.FromBytes(m.Payload);
                }
            };
            b.GameplayCommandReceived += m =>
            {
                if (m.Kind == ModDataCommand.Kind)
                {
                    bGot = ModDataCommand.FromBytes(m.Payload);
                }
            };

            a.SendGameplayCommand(ModDataCommand.Kind, new ModDataCommand { Target = new EntityRef { Kind = EntityKind.NetNode }, TypeName = "T", ElementSize = 2, Data = new byte[] { 7, 9 } }.ToBytes());
            Assert.True(loop.PumpUntil(() => bGot != null));
            Assert.Equal(new byte[] { 7, 9 }, bGot.Data);

            b.SendGameplayCommand(BuildResultCommand.Kind, new BuildResultCommand { Sequence = 5, BuilderPlayerId = a.LocalPlayerId, ToolId = "Net Tool", Ok = false, Message = "no road there" }.ToBytes());
            Assert.True(loop.PumpUntil(() => aResult != null));
            Assert.Equal(5, aResult.Sequence);
            Assert.Equal("no road there", aResult.Message);

            loop.PumpFor(100);
            Assert.Equal(0, aGot);
        }
    }
}
