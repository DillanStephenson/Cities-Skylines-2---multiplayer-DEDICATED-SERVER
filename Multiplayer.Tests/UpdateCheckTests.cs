using System;
using Multiplayer.Core.Session;
using Xunit;

namespace Multiplayer.Tests
{
    /// <summary>Parsing GitHub's latest-release answer and deciding whether it is newer than what runs.</summary>
    public class UpdateCheckTests
    {
        private const string Latest = "{\"url\":\"https://api.github.com/repos/DillanStephenson/Cities-Skylines-2---multiplayer-DEDICATED-SERVER/releases/1\",\"html_url\":\"https://github.com/DillanStephenson/Cities-Skylines-2---multiplayer-DEDICATED-SERVER/releases/tag/v0.2.0\","
            + "\"tag_name\":\"v0.2.0\",\"name\":\"v0.2.0\",\"draft\":false,\"prerelease\":false,\"published_at\":\"2026-09-17T14:25:10Z\","
            + "\"assets\":[{\"name\":\"cs2-multiplayer-server-linux-x64-v0.2.0.zip\",\"browser_download_url\":\"https://github.com/x/y/releases/download/v0.2.0/a.zip\"}]}";

        [Fact]
        public void ParsesTheLatestRelease()
        {
            ReleaseInfo release = GitHubReleases.Parse(Latest);
            Assert.Equal("v0.2.0", release.Tag);
            Assert.Equal(new Version(0, 2, 0, 0), release.Version);
            Assert.Equal("https://github.com/DillanStephenson/Cities-Skylines-2---multiplayer-DEDICATED-SERVER/releases/tag/v0.2.0", release.Url);
            Assert.Equal("2026-09-17T14:25:10Z", release.PublishedAt);
        }

        [Fact]
        public void AnErrorAnswer_ThrowsWithItsMessage()
        {
            var ex = Assert.Throws<FormatException>(() => GitHubReleases.Parse("{\"message\":\"Not Found\",\"documentation_url\":\"x\"}"));
            Assert.Contains("Not Found", ex.Message);
        }

        [Theory]
        [InlineData("v0.2.1", "0.2.0", true)]
        [InlineData("v0.3", "0.2.9", true)]
        [InlineData("1.0.0", "0.2.0", true)]
        [InlineData("v0.2.0", "0.2.0", false)]
        [InlineData("v0.1.9", "0.2.0", false)]
        [InlineData("release-0.2.0-beta", "0.2.0", false)]
        [InlineData("nightly", "0.2.0", false)]
        public void ComparesTagsAgainstTheRunningVersion(string tag, string current, bool newer)
        {
            Assert.Equal(newer, GitHubReleases.IsNewer(tag, current));
        }

        [Fact]
        public void VersionParsing_IsForgiving()
        {
            Assert.True(GitHubReleases.TryParseVersion("v1.4.2-beta3", out Version v));
            Assert.Equal(new Version(1, 4, 2, 0), v);
            Assert.False(GitHubReleases.TryParseVersion("", out _));
            Assert.False(GitHubReleases.TryParseVersion("latest", out _));
        }
    }
}
