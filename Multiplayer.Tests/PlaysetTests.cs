using System.Collections.Generic;
using Multiplayer.Core.Session;
using Multiplayer.Core.Util;
using Xunit;

namespace Multiplayer.Tests
{
    /// <summary>The JSON reader and the Paradox playset parsing behind the linked-playset feature.</summary>
    public class PlaysetTests
    {
        private const string Details = "{\"gameName\":\"cities_skylines_2\",\"id\":11843013,\"modsCount\":27,\"modsSize\":2329438305,\"name\":\"Shared Mods\","
            + "\"updated\":\"2026-09-16T20:02:13.000Z\",\"latestModUpdate\":\"2026-09-16T07:59:13.000Z\",\"subscribersCount\":1,\"owned\":false,\"state\":\"public\","
            + "\"forumLink\":null,\"description\":null,\"author\":\"MLGgeneralPingu\",\"publicVersions\":[{\"displayName\":\"Shared Mods\",\"version\":2,\"modsCount\":27},"
            + "{\"displayName\":\"Shared Mods\",\"version\":1,\"modsCount\":16}],\"latestPublicVersion\":2,\"result\":\"OK\"}";

        private const string Page = "{\"count\":3,\"mods\":[" +
            "{\"state\":\"published\",\"displayName\":\"CS2 Multiplayer Mod\",\"latestVersion\":\"12\",\"modId\":150432,\"tags\":[\"Code Mod\"],\"enabled\":true,\"size\":841371}," +
            "{\"state\":\"published\",\"displayName\":\"Aldi Supermarket 2000'\",\"latestVersion\":\"3\",\"modId\":158529,\"enabled\":true,\"shortDescription\":\"A \\\"classic\\\" brick-style [store]\"}," +
            "{\"state\":\"published\",\"displayName\":\"Multiplayer\",\"latestVersion\":\"1\",\"modId\":999999,\"enabled\":true}," +
            "{\"state\":\"published\",\"displayName\":\"Disabled Thing\",\"latestVersion\":\"4\",\"modId\":4242,\"enabled\":false}" +
            "]}";

        [Fact]
        public void MiniJson_ReadsObjectsArraysStringsNumbersAndEscapes()
        {
            object root = MiniJson.Parse("{\"a\": [1, 2.5, -3e2, true, false, null, \"x\\ny\\u0041\"], \"b\": {\"c\": \"d\"}, \"e\": \"\"}");
            List<object> a = MiniJson.AsArray(MiniJson.Get(root, "a"));
            Assert.Equal(7, a.Count);
            Assert.Equal(1d, a[0]);
            Assert.Equal(2.5d, a[1]);
            Assert.Equal(-300d, a[2]);
            Assert.Equal(true, a[3]);
            Assert.Equal(false, a[4]);
            Assert.Null(a[5]);
            Assert.Equal("x\nyA", a[6]);
            Assert.Equal("d", MiniJson.GetString(MiniJson.Get(root, "b"), "c"));
            Assert.Equal("", MiniJson.GetString(root, "e", "fallback"));
            Assert.Equal("fallback", MiniJson.GetString(root, "missing", "fallback"));
            Assert.Equal(7, MiniJson.GetInt(root, "missing", 7));
        }

        [Fact]
        public void MiniJson_RejectsGarbage()
        {
            Assert.Throws<System.FormatException>(() => MiniJson.Parse("{\"a\": }"));
            Assert.Throws<System.FormatException>(() => MiniJson.Parse("[1, 2"));
            Assert.Throws<System.FormatException>(() => MiniJson.Parse("{} extra"));
        }

        [Fact]
        public void ParsesPlaysetDetails()
        {
            PlaysetSnapshot snapshot = ParadoxPlayset.ParseDetails(Details, 11843013);
            Assert.Equal(11843013, snapshot.Id);
            Assert.Equal("Shared Mods", snapshot.Name);
            Assert.Equal(2, snapshot.Version);
            Assert.Equal(27, snapshot.ModsCount);
            Assert.Equal("2026-09-16T20:02:13.000Z", snapshot.Updated);
            Assert.Equal("Shared Mods v2 (Paradox playset 11843013)", snapshot.Label);
        }

        [Fact]
        public void ParsesModsPage_SkippingOwnModAndDisabledEntries()
        {
            var snapshot = new PlaysetSnapshot { Id = 11843013 };
            int total = ParadoxPlayset.AddMods(snapshot, Page);
            Assert.Equal(3, total);
            Assert.Equal(new[] { "CS2 Multiplayer Mod [pdx 150432 v12]", "Aldi Supermarket 2000' [pdx 158529 v3]" }, snapshot.Mods);
        }

        [Fact]
        public void PlaysetEntries_MatchTheGamesOwnFormat()
        {
            // What the game sends for the same mod, from its playset_config.json and pdx_mods folder.
            string[] fromGame = { "AldiSupermarket [pdx 158529 v3]", "CS2MultiplayerMod [pdx 150432 v12]" };
            var snapshot = new PlaysetSnapshot();
            ParadoxPlayset.AddMods(snapshot, Page);

            // Names differ (display name vs dll name) but the Paradox ids pair up, so in the default mode it is a match.
            Assert.Null(ModListCompare.Describe(snapshot.Mods, fromGame, null, ignoreVersions: true));
            Assert.Contains("missing: Aldi Supermarket", ModListCompare.Describe(snapshot.Mods, new[] { "CS2MultiplayerMod [pdx 150432 v12]" }, null, ignoreVersions: true));
        }

        [Fact]
        public void AddedNames_ComparesByParadoxId()
        {
            string[] before = { "A [pdx 1 v1]", "B [pdx 2 v1]" };
            string[] after = { "A renamed [pdx 1 v2]", "C [pdx 3 v1]", "B [pdx 2 v1]" };
            Assert.Equal(new[] { "C" }, ParadoxPlayset.AddedNames(before, after));
            Assert.Empty(ParadoxPlayset.AddedNames(after, before).FindAll(n => n == "A"));
            Assert.Equal(new List<string>(), ParadoxPlayset.AddedNames(after, before));
        }

        [Fact]
        public void LockedReference_IsNotReplacedByTheOwner_ButTheOwnerIsWarned()
        {
            using var loop = new Loopback();
            loop.StartServer();
            loop.Server.SetReferenceMods(new[] { "Aldi [pdx 158529 v3]", "Lidl [pdx 158544 v3]" }, "Shared Mods v2 (Paradox playset 11843013)", locked: true);

            string warning = null;
            ClientConfig ownerConfig = Loopback.FastClientConfig("Host", "", Loopback.OwnerKey, new[] { "Aldi [pdx 158529 v3]" });
            ClientSession owner = loop.StartClient(ownerConfig);
            owner.ChatReceived += (player, text) => warning = text;
            Assert.True(loop.PumpUntil(() => owner.State == SessionState.Connected), owner.LastError);
            Assert.True(loop.PumpUntil(() => warning != null));

            Assert.Contains("Lidl [pdx 158544 v3]", loop.Server.ReferenceMods);
            Assert.Equal("Shared Mods v2 (Paradox playset 11843013)", loop.Server.ReferencePlayset);
            Assert.Contains("differ from the published playset", warning);
            Assert.Contains("Lidl", warning);
        }
    }
}
