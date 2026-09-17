using Multiplayer.Core.Session;
using Xunit;

namespace Multiplayer.Tests
{
    /// <summary>The mod-list gate: same mods at any version by default, strict on request, with a playset hint.</summary>
    public class ModCheckTests
    {
        private static readonly string[] HostMods = { "Traffic Tool Essentials [pdx 125342 v5]", "Big Box Pack [pdx 135036 v4]", "Dev Build [local]" };
        private static readonly string[] OlderVersions = { "Traffic Tool Essentials [pdx 125342 v4]", "Big Box Pack [pdx 135036 v3]", "Dev Build [local]" };
        private static readonly string[] MissingOne = { "Traffic Tool Essentials [pdx 125342 v5]", "Dev Build [local]" };

        [Fact]
        public void IgnoringVersions_MatchesSameModsAtOtherVersions()
        {
            Assert.NotNull(ModListCompare.Describe(HostMods, OlderVersions, null));
            Assert.Null(ModListCompare.Describe(HostMods, OlderVersions, null, ignoreVersions: true));
        }

        [Fact]
        public void IgnoringVersions_StillReportsMissingAndExtra()
        {
            string text = ModListCompare.Describe(HostMods, MissingOne, null, ignoreVersions: true);
            Assert.Contains("missing: Big Box Pack", text);
            Assert.DoesNotContain("version differs", text);

            string[] extra = { "Traffic Tool Essentials [pdx 125342 v4]", "Big Box Pack [pdx 135036 v4]", "Dev Build [local]", "Cheats [pdx 7 v1]" };
            string text2 = ModListCompare.Describe(HostMods, extra, null, ignoreVersions: true);
            Assert.Contains("extra: Cheats", text2);
            Assert.DoesNotContain("version differs", text2);
        }

        [Fact]
        public void Server_DefaultMode_AcceptsOtherVersions_AndStrictRejectsThem()
        {
            using (var loop = new Loopback())
            {
                loop.StartServer();
                ClientSession owner = loop.StartClient(Loopback.FastClientConfig("Host", "", Loopback.OwnerKey, HostMods));
                Assert.True(loop.PumpUntil(() => owner.State == SessionState.Connected), owner.LastError);

                ClientSession guest = loop.StartClient(Loopback.FastClientConfig("Guest", "", "", OlderVersions));
                Assert.True(loop.PumpUntil(() => guest.State == SessionState.Connected), guest.LastError);
            }

            using (var loop = new Loopback())
            {
                ServerConfig config = Loopback.FastServerConfig();
                config.IgnoreModVersions = false;
                loop.StartServer(config);
                ClientSession owner = loop.StartClient(Loopback.FastClientConfig("Host", "", Loopback.OwnerKey, HostMods));
                Assert.True(loop.PumpUntil(() => owner.State == SessionState.Connected), owner.LastError);

                ClientSession guest = loop.StartClient(Loopback.FastClientConfig("Guest", "", "", OlderVersions));
                Assert.True(loop.PumpUntil(() => guest.State == SessionState.Failed));
                Assert.Contains("version differs", guest.LastError);
            }
        }

        [Fact]
        public void Rejection_NamesTheHostPlaysetAndHint()
        {
            using var loop = new Loopback();
            ServerConfig config = Loopback.FastServerConfig();
            config.PlaysetHint = "Paradox playset 11843013";
            loop.StartServer(config);

            ClientConfig ownerConfig = Loopback.FastClientConfig("Host", "", Loopback.OwnerKey, HostMods);
            ownerConfig.Playset = "Shared Mods";
            ClientSession owner = loop.StartClient(ownerConfig);
            Assert.True(loop.PumpUntil(() => owner.State == SessionState.Connected), owner.LastError);
            Assert.Equal("Shared Mods", loop.Server.ReferencePlayset);

            ClientSession guest = loop.StartClient(Loopback.FastClientConfig("Guest", "", "", MissingOne));
            Assert.True(loop.PumpUntil(() => guest.State == SessionState.Failed));
            Assert.Contains("missing: Big Box Pack", guest.LastError);
            Assert.Contains("playset 'Shared Mods' (Paradox playset 11843013)", guest.LastError);
            Assert.Contains("activate it in Paradox Mods", guest.LastError);
        }

        [Fact]
        public void OwnerRejoin_ReplacesTheStoredListAndPlayset()
        {
            using var loop = new Loopback();
            loop.StartServer();
            loop.Server.SetReferenceMods(new[] { "Old Pack [pdx 1 v1]" }, "Old Playset");

            ClientConfig ownerConfig = Loopback.FastClientConfig("Host", "", Loopback.OwnerKey, HostMods);
            ownerConfig.Playset = "Shared Mods";
            ClientSession owner = loop.StartClient(ownerConfig);
            Assert.True(loop.PumpUntil(() => owner.State == SessionState.Connected), owner.LastError);

            Assert.Equal("Shared Mods", loop.Server.ReferencePlayset);
            Assert.Contains("Big Box Pack [pdx 135036 v4]", loop.Server.ReferenceMods);
            Assert.DoesNotContain("Old Pack [pdx 1 v1]", loop.Server.ReferenceMods);
        }
    }
}
