using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Multiplayer.Tests
{
    /// <summary>The server.json settings file: keys, mod list shapes, tolerance for the unknown, and the template.</summary>
    public class ServerConfigTests
    {
        private static readonly string ServerSources = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Multiplayer.Server"));

        [Fact]
        public void ExampleFile_IsReadableAndCovered()
        {
            string example = File.ReadAllText(Path.Combine(ServerSources, "..", "deploy", "server.example.json"));
            object root = Multiplayer.Core.Util.MiniJson.Parse(example);
            var map = Multiplayer.Core.Util.MiniJson.AsObject(root);
            Assert.NotNull(map);
            Assert.Equal("Our server", Multiplayer.Core.Util.MiniJson.GetString(root, "name"));
            Assert.Equal(27015, Multiplayer.Core.Util.MiniJson.GetInt(root, "port"));
            Assert.Contains("requiredMods", map.Keys);
            Assert.Contains("welcome", map.Keys);
            Assert.Contains("playsetId", map.Keys);
        }

        [Theory]
        [InlineData("125342", "Mod 125342 [pdx 125342 v0]")]
        [InlineData(" 158529 ", "Mod 158529 [pdx 158529 v0]")]
        [InlineData("Traffic Tool Essentials [pdx 125342 v5]", "Traffic Tool Essentials [pdx 125342 v5]")]
        [InlineData("Road Builder [local]", "Road Builder [local]")]
        [InlineData("My Dev Mod", "My Dev Mod [local]")]
        [InlineData("", "")]
        public void ModEntries_TakeIdsNamesOrFullEntries(string input, string expected)
        {
            Assert.Equal(expected, NormalizeModEntry(input));
        }

        [Fact]
        public void IdOnlyEntries_MatchTheGamesListInNamesMode()
        {
            string[] fromFile = { NormalizeModEntry("125342"), NormalizeModEntry("158529") };
            string[] fromGame = { "Traffic Tool Essentials [pdx 125342 v5]", "AldiSupermarket [pdx 158529 v3]" };
            Assert.Null(Multiplayer.Core.Session.ModListCompare.Describe(fromFile, fromGame, null, ignoreVersions: true));
            Assert.Contains("missing", Multiplayer.Core.Session.ModListCompare.Describe(fromFile, new[] { fromGame[0] }, null, ignoreVersions: true));
        }

        private static string NormalizeModEntry(string entry)
        {
            return Multiplayer.Core.Session.ModListCompare.NormalizeEntry(entry);
        }
    }
}
