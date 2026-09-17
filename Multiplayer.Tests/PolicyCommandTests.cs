using Multiplayer.Core.Build;
using Multiplayer.Core.Protocol;
using Xunit;

namespace Multiplayer.Tests
{
    public class PolicyCommandTests
    {
        [Fact]
        public void RoundTripsEveryField()
        {
            var command = new PolicyCommand
            {
                Target = new EntityRef { Kind = EntityKind.Object, Prefab = new PrefabKey { Type = "BuildingPrefab", Name = "Fire House" }, Position = new Vec3(10, 20, 30) },
                Policy = new PrefabKey { Type = "PolicyPrefab", Name = "Inactive" },
                Active = true,
                Adjustment = 0.75f,
            };

            PolicyCommand copy = PolicyCommand.FromBytes(command.ToBytes());

            Assert.Equal(EntityKind.Object, copy.Target.Kind);
            Assert.Equal("Fire House", copy.Target.Prefab.Name);
            Assert.Equal(10f, copy.Target.Position.X);
            Assert.Equal(30f, copy.Target.Position.Z);
            Assert.Equal("PolicyPrefab", copy.Policy.Type);
            Assert.Equal("Inactive", copy.Policy.Name);
            Assert.True(copy.Active);
            Assert.Equal(0.75f, copy.Adjustment);
        }

        [Fact]
        public void DescribesItself()
        {
            var command = new PolicyCommand
            {
                Target = new EntityRef { Kind = EntityKind.Area, Prefab = new PrefabKey { Type = "DistrictPrefab", Name = "District" }, Position = new Vec3(1, 2, 3) },
                Policy = new PrefabKey { Type = "PolicyPrefab", Name = "Heavy Traffic Ban" },
                Active = false,
            };

            string text = command.ToString();

            Assert.Contains("Heavy Traffic Ban", text);
            Assert.Contains("off", text);
            Assert.Contains("District", text);
        }

        [Fact]
        public void RejectsGarbage()
        {
            Assert.Throws<ProtocolException>(() => PolicyCommand.FromBytes(new byte[] { 9, 1, 2 }));
            Assert.Throws<ProtocolException>(() => PolicyCommand.FromBytes(new byte[] { PolicyCommand.Version, 1 }));
        }
    }
}
