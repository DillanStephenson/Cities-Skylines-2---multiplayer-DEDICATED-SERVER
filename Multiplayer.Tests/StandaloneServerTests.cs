using Multiplayer.Core.Session;
using Xunit;

namespace Multiplayer.Tests
{
    /// <summary>A dedicated server outlives the host's game; only the window the game spawned dies with it.</summary>
    public class StandaloneServerTests
    {
        [Fact]
        public void OwnerStopRequest_IsIgnored_WhenTheOwnerMayNotStop()
        {
            using var loop = new Loopback();
            ServerConfig config = Loopback.FastServerConfig();
            config.OwnerMayStop = false;
            loop.StartServer(config);

            ClientSession owner = loop.StartOwner();
            ClientSession guest = loop.StartClient("Guest");
            Assert.True(loop.PumpUntil(() => owner.State == SessionState.Connected && guest.State == SessionState.Connected), owner.LastError + " / " + guest.LastError);

            owner.RequestServerStop("Host game closed");
            owner.Leave("Host game closed");
            loop.PumpFor(300);

            Assert.True(loop.Server.IsRunning);
            Assert.Equal(SessionState.Connected, guest.State);
            Assert.Single(loop.Server.Players);
        }

        [Fact]
        public void OwnerStopRequest_StopsTheServer_WhenAllowed()
        {
            using var loop = new Loopback();
            loop.StartServer();
            ClientSession owner = loop.StartOwner();
            Assert.True(loop.PumpUntil(() => owner.State == SessionState.Connected), owner.LastError);

            owner.RequestServerStop("Host game closed");
            Assert.True(loop.PumpUntil(() => !loop.Server.IsRunning));
        }
    }
}
