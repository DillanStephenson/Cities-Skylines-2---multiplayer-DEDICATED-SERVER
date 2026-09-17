using Multiplayer.Core.Build;
using Multiplayer.Core.Protocol;
using Xunit;

namespace Multiplayer.Tests
{
    public class CityStateCommandTests
    {
        [Fact]
        public void RoundTrips()
        {
            var original = new CityStateCommand
            {
                FullSnapshot = true,
                Entries =
                {
                    new StateEntry("tax:area:Residential", 12f),
                    new StateEntry("budget:ServicePrefab|Healthcare", 80f),
                    new StateEntry("fee:Electricity", 0.13f),
                    new StateEntry("policy:CityPolicyPrefab|Free Parking:active", 1f),
                },
            };

            CityStateCommand decoded = CityStateCommand.FromBytes(original.ToBytes());
            Assert.True(decoded.FullSnapshot);
            Assert.Equal(4, decoded.Entries.Count);
            Assert.Equal("fee:Electricity", decoded.Entries[2].Key);
            Assert.Equal(0.13f, decoded.Entries[2].Value);
            Assert.Equal(1f, decoded.Entries[3].Value);
        }

        [Fact]
        public void Malformed_Throws()
        {
            Assert.Throws<ProtocolException>(() => CityStateCommand.FromBytes(new byte[] { 9 }));
            byte[] bytes = new CityStateCommand { Entries = { new StateEntry("a", 1f) } }.ToBytes();
            var cut = new byte[bytes.Length - 2];
            System.Array.Copy(bytes, cut, cut.Length);
            Assert.Throws<ProtocolException>(() => CityStateCommand.FromBytes(cut));
        }
    }
}
