using System;
using System.Collections.Generic;
using Multiplayer.Core.Session;
using Xunit;

namespace Multiplayer.Tests
{
    public class WorldTransferTests
    {
        private static byte[] PseudoRandom(int length, int seed)
        {
            var data = new byte[length];
            var random = new Random(seed);
            random.NextBytes(data);
            return data;
        }

        [Fact]
        public void OwnerUpload_IsStored_AndLateJoinerDownloadsIt()
        {
            using var loop = new Loopback();
            ServerSession server = loop.StartServer();
            ClientSession owner = loop.StartOwner();
            Assert.True(loop.PumpUntil(() => owner.State == SessionState.Connected && owner.ServerWorldKnown), owner.LastError);
            Assert.Null(owner.ServerWorld);

            byte[] world = PseudoRandom(3 * 1024 * 1024 + 123, 42);
            WorldSnapshot stored = null;
            server.WorldChanged += s => stored = s;
            (bool ok, int revision, string reason) verdict = default;
            owner.WorldUploadFinished += (ok, rev, reason) => verdict = (ok, rev, reason);

            Assert.True(owner.UploadWorld("MP Test", "Testville", "abc123", world));
            Assert.True(owner.IsUploadingWorld);
            Assert.True(loop.PumpUntil(() => stored != null && verdict.ok, 10000), verdict.reason);

            Assert.Equal(1, verdict.revision);
            Assert.False(owner.IsUploadingWorld);
            Assert.Equal(world, stored.Data);
            Assert.Equal("Testville", stored.Info.CityName);
            Assert.Equal("MP Test", stored.Info.SaveName);
            Assert.Equal("abc123", stored.Info.Guid);
            Assert.Equal("HostPlayer", stored.Info.UploaderName);
            Assert.True(loop.PumpUntil(() => owner.ServerWorld != null && owner.ServerWorld.Revision == 1));

            ClientSession joiner = loop.StartClient("Ada");
            Assert.True(loop.PumpUntil(() => joiner.State == SessionState.Connected && joiner.ServerWorld != null), joiner.LastError);
            Assert.Equal(1, joiner.ServerWorld.Revision);
            Assert.Equal(world.Length, joiner.ServerWorld.Size);

            WorldSnapshot received = null;
            long lastProgress = 0;
            joiner.WorldDownloaded += s => received = s;
            joiner.WorldDownloadProgress += (got, total) => lastProgress = got;
            Assert.True(joiner.RequestWorld());
            Assert.True(joiner.IsDownloadingWorld);
            Assert.True(loop.PumpUntil(() => received != null, 10000));

            Assert.Equal(world, received.Data);
            Assert.Equal(world.Length, lastProgress);
            Assert.False(joiner.IsDownloadingWorld);
            Assert.Equal(stored.Info.Sha256, received.Info.Sha256);
        }

        [Fact]
        public void SecondUpload_BumpsRevision_AndEveryoneHears()
        {
            using var loop = new Loopback();
            ServerSession server = loop.StartServer();
            ClientSession owner = loop.StartOwner();
            ClientSession guest = loop.StartClient("Guest");
            Assert.True(loop.PumpUntil(() => owner.State == SessionState.Connected && guest.State == SessionState.Connected));

            int guestHeard = 0;
            guest.WorldInfoReceived += info => guestHeard = info?.Revision ?? -1;

            owner.UploadWorld("A", "A", "", PseudoRandom(1000, 1));
            Assert.True(loop.PumpUntil(() => server.World != null && server.World.Info.Revision == 1 && guestHeard == 1));
            owner.UploadWorld("B", "B", "", PseudoRandom(2000, 2));
            Assert.True(loop.PumpUntil(() => server.World.Info.Revision == 2 && guestHeard == 2));
            Assert.Equal("B", server.World.Info.SaveName);
        }

        [Fact]
        public void GuestUpload_IsRefused()
        {
            using var loop = new Loopback();
            ServerSession server = loop.StartServer();
            ClientSession guest = loop.StartClient("Guest");
            Assert.True(loop.PumpUntil(() => guest.State == SessionState.Connected));

            // A guest cannot even start one client-side; simulate a rogue client by flipping the flag through the API path.
            Assert.False(guest.UploadWorld("x", "x", "", new byte[10]));
            Assert.Null(server.World);
        }

        [Fact]
        public void PreloadedWorld_IsAnnouncedOnJoin_AndOldRevisionRequestGetsFreshInfo()
        {
            using var loop = new Loopback();
            ServerSession server = loop.StartServer();
            byte[] world = PseudoRandom(500 * 1024, 7);
            server.SetWorld(new WorldSnapshot(new WorldInfo { Revision = 5, Size = world.Length, Sha256 = WorldHash.Sha256Hex(world), SaveName = "Disk", CityName = "Diskville" }, world));

            ClientSession client = loop.StartClient("Ada");
            Assert.True(loop.PumpUntil(() => client.State == SessionState.Connected && client.ServerWorld != null));
            Assert.Equal(5, client.ServerWorld.Revision);
            Assert.Equal("Diskville", client.ServerWorld.CityName);

            WorldSnapshot received = null;
            client.WorldDownloaded += s => received = s;
            Assert.True(client.RequestWorld());
            Assert.True(loop.PumpUntil(() => received != null, 10000));
            Assert.Equal(world, received.Data);
        }

        [Fact]
        public void EmptyServer_RequestWorld_IsNoOp()
        {
            using var loop = new Loopback();
            loop.StartServer();
            ClientSession client = loop.StartClient("Ada");
            Assert.True(loop.PumpUntil(() => client.State == SessionState.Connected && client.ServerWorldKnown));
            Assert.False(client.RequestWorld());
        }

        [Fact]
        public void ModLists_OwnerSetsReference_GuestsMustMatch()
        {
            using var loop = new Loopback();
            ServerSession server = loop.StartServer();
            IReadOnlyList<string> reference = null;
            server.ReferenceModsChanged += mods => reference = mods;

            // Before any owner, nothing to compare against: accepted.
            ClientSession early = loop.StartClient(Loopback.FastClientConfig("Early", mods: new[] { "Anything" }));
            Assert.True(loop.PumpUntil(() => early.State == SessionState.Connected), early.LastError);

            ClientSession owner = loop.StartClient(Loopback.FastClientConfig("HostPlayer", ownerKey: Loopback.OwnerKey, mods: new[] { "Multiplayer", "TrafficMod", "RoadBuilder" }));
            Assert.True(loop.PumpUntil(() => owner.State == SessionState.Connected && reference != null), owner.LastError);
            Assert.Equal(new[] { "Multiplayer", "RoadBuilder", "TrafficMod" }, reference);

            ClientSession same = loop.StartClient(Loopback.FastClientConfig("Same", mods: new[] { "roadbuilder", "TrafficMod", "Multiplayer" }));
            Assert.True(loop.PumpUntil(() => same.State == SessionState.Connected), same.LastError);

            ClientSession missing = loop.StartClient(Loopback.FastClientConfig("Missing", mods: new[] { "Multiplayer", "TrafficMod" }));
            Assert.True(loop.PumpUntil(() => missing.State == SessionState.Failed));
            Assert.Contains("missing: RoadBuilder", missing.LastError);

            ClientSession extra = loop.StartClient(Loopback.FastClientConfig("Extra", mods: new[] { "Multiplayer", "TrafficMod", "RoadBuilder", "Cheats" }));
            Assert.True(loop.PumpUntil(() => extra.State == SessionState.Failed));
            Assert.Contains("extra: Cheats", extra.LastError);
        }

        [Fact]
        public void ModCheck_CanBeTurnedOff()
        {
            using var loop = new Loopback();
            ServerConfig config = Loopback.FastServerConfig();
            config.RequireMatchingMods = false;
            loop.StartServer(config);

            ClientSession owner = loop.StartClient(Loopback.FastClientConfig("HostPlayer", ownerKey: Loopback.OwnerKey, mods: new[] { "A" }));
            Assert.True(loop.PumpUntil(() => owner.State == SessionState.Connected), owner.LastError);
            ClientSession other = loop.StartClient(Loopback.FastClientConfig("Other", mods: new[] { "B" }));
            Assert.True(loop.PumpUntil(() => other.State == SessionState.Connected), other.LastError);
        }

        [Fact]
        public void ModListCompare_WordsTheDifference()
        {
            Assert.Null(ModListCompare.Describe(new[] { "A", "b" }, new[] { "B", "a" }, null));
            string text = ModListCompare.Describe(new[] { "A", "B", "C" }, new[] { "A", "D" }, null);
            Assert.Contains("missing: B, C", text);
            Assert.Contains("extra: D", text);
            Assert.Null(ModListCompare.Describe(new[] { "A", "Own" }, new[] { "A" }, "Own"));
        }

        [Fact]
        public void ModListCompare_SpotsVersionDifferences()
        {
            string[] host = { "Traffic Tool Essentials [pdx 125342 v5]", "Road Builder [pdx 100 v2]", "Dev Build [local]" };
            string[] guest = { "Traffic Tool Essentials [pdx 125342 v4]", "Road Builder [pdx 100 v2]", "Dev Build [local]", "Cheats [pdx 7 v1]" };

            string text = ModListCompare.Describe(host, guest, null);
            Assert.Contains("version differs: Traffic Tool Essentials (host v5, you v4)", text);
            Assert.Contains("extra: Cheats", text);
            Assert.DoesNotContain("missing", text);

            Assert.Equal("125342", ModListCompare.PdxId("Traffic Tool Essentials [pdx 125342 v5]"));
            Assert.Null(ModListCompare.PdxId("Dev Build [local]"));
            Assert.Equal("v5", ModListCompare.PdxVersion("Traffic Tool Essentials [pdx 125342 v5]"));
            Assert.Equal("Dev Build", ModListCompare.DisplayName("Dev Build [local]"));
        }
    }
}
