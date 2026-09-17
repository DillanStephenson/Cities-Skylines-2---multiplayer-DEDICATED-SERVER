using System;
using System.Globalization;
using System.Text;
using Multiplayer.Core.Util;

namespace Multiplayer.Core.Session
{
    /// <summary>The newest release of the project on GitHub, as far as the update check is concerned.</summary>
    public sealed class ReleaseInfo
    {
        public string Tag = string.Empty;
        public Version Version;
        public string Url = string.Empty;
        public string PublishedAt = string.Empty;
    }

    /// <summary>
    /// Reads the GitHub "latest release" answer and compares release tags with the running version. The mod
    /// and the server share one version number (ProtocolConstants.ModVersion), so one check covers both.
    /// </summary>
    public static class GitHubReleases
    {
        public const string Repository = "DillanStephenson/cs2-multiplayer";

        public static string LatestUrl(string repository)
        {
            return "https://api.github.com/repos/" + repository + "/releases/latest";
        }

        public static string ReleasesPage(string repository)
        {
            return "https://github.com/" + repository + "/releases";
        }

        public static ReleaseInfo Parse(string json)
        {
            object root = MiniJson.Parse(json);
            if (MiniJson.AsObject(root) == null)
            {
                throw new FormatException("Release answer is not a JSON object");
            }

            string tag = MiniJson.GetString(root, "tag_name", string.Empty).Trim();
            if (tag.Length == 0)
            {
                string message = MiniJson.GetString(root, "message", string.Empty);
                throw new FormatException(message.Length > 0 ? message : "Release answer has no tag");
            }

            var info = new ReleaseInfo
            {
                Tag = tag,
                Url = MiniJson.GetString(root, "html_url", string.Empty),
                PublishedAt = MiniJson.GetString(root, "published_at", string.Empty),
            };
            TryParseVersion(tag, out info.Version);
            return info;
        }

        /// <summary>"v0.2.1", "0.2", "release-1.4.0-beta" all give a Version; anything without digits does not.</summary>
        public static bool TryParseVersion(string text, out Version version)
        {
            version = null;
            var digits = new StringBuilder();
            bool started = false;
            foreach (char c in text ?? string.Empty)
            {
                if (char.IsDigit(c))
                {
                    started = true;
                    digits.Append(c);
                }
                else if (c == '.' && started)
                {
                    digits.Append('.');
                }
                else if (started)
                {
                    break;
                }
            }

            string clean = digits.ToString().TrimEnd('.');
            if (clean.Length == 0)
            {
                return false;
            }

            string[] parts = clean.Split('.');
            var numbers = new int[4];
            for (int i = 0; i < 4; i++)
            {
                numbers[i] = i < parts.Length && int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
            }

            version = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
            return true;
        }

        /// <summary>True when the release tag names a version above the one running.</summary>
        public static bool IsNewer(string releaseTag, string currentVersion)
        {
            return TryParseVersion(releaseTag, out Version latest) && TryParseVersion(currentVersion, out Version current) && latest > current;
        }
    }
}
