using Multiplayer.Core.Session;
using Xunit;

namespace Multiplayer.Tests
{
    /// <summary>While a release rolls out, a server can let in the previous mod version as well as its own.</summary>
    public class CompatibleVersionsTests
    {
        [Fact]
        public void ListedOlderVersion_IsAccepted_UnlistedOneIsNot()
        {
            using var loop = new Loopback();
            ServerConfig config = Loopback.FastServerConfig();
            config.ExtraModVersions.Add("0.2.3");
            loop.StartServer(config);

            ClientConfig older = Loopback.FastClientConfig("Older");
            older.ModVersion = "0.2.3";
            ClientSession olderClient = loop.StartClient(older);
            Assert.True(loop.PumpUntil(() => olderClient.State == SessionState.Connected), olderClient.LastError);

            ClientConfig ancient = Loopback.FastClientConfig("Ancient");
            ancient.ModVersion = "0.1.0";
            ClientSession ancientClient = loop.StartClient(ancient);
            Assert.True(loop.PumpUntil(() => ancientClient.State == SessionState.Failed));
            Assert.Contains("Mod version mismatch", ancientClient.LastError);
        }
    }
}
