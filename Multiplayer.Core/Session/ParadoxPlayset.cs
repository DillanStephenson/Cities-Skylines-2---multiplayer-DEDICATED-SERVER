using System;
using System.Collections.Generic;
using Multiplayer.Core.Util;

namespace Multiplayer.Core.Session
{
    /// <summary>What a published Paradox playset contains, read from the public Paradox Mods API.</summary>
    public sealed class PlaysetSnapshot
    {
        public int Id;
        public string Name = string.Empty;
        public int Version;
        public string Updated = string.Empty;
        public int ModsCount;
        public List<string> Mods = new List<string>();

        /// <summary>How the reference is labelled for players: "Shared Mods v2 (Paradox playset 11843013)".</summary>
        public string Label => Name + " v" + Version + " (Paradox playset " + Id + ")";
    }

    /// <summary>
    /// Turns the Paradox Mods API answers for a playset into the mod list format the handshake uses
    /// ("Name [pdx ID vN]"). The API needs no login for public playsets:
    /// GET mods/sets/{id} for the details and GET mods/sets/{id}/mods?limit=100&amp;page=N for the content.
    /// </summary>
    public static class ParadoxPlayset
    {
        public const string ApiBase = "https://api.paradox-interactive.com/";
        public const int PageSize = 100;

        /// <summary>Mods with this display name are left out of the reference: every player has this mod by definition.</summary>
        public const string OwnModName = "Multiplayer";

        public static string DetailsUrl(int playsetId)
        {
            return ApiBase + "mods/sets/" + playsetId;
        }

        public static string ModsUrl(int playsetId, int page, int version)
        {
            return ApiBase + "mods/sets/" + playsetId + "/mods?limit=" + PageSize + "&page=" + page + (version > 0 ? "&version=" + version : string.Empty);
        }

        public static PlaysetSnapshot ParseDetails(string json, int playsetId)
        {
            object root = MiniJson.Parse(json);
            if (MiniJson.AsObject(root) == null)
            {
                throw new FormatException("Playset details are not a JSON object");
            }

            string result = MiniJson.GetString(root, "result", "OK");
            if (!string.Equals(result, "OK", StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException("Playset lookup failed: " + result);
            }

            return new PlaysetSnapshot
            {
                Id = MiniJson.GetInt(root, "id", playsetId),
                Name = MiniJson.GetString(root, "name", "Playset " + playsetId).Trim(),
                Version = MiniJson.GetInt(root, "latestPublicVersion", 0),
                Updated = MiniJson.GetString(root, "updated", string.Empty),
                ModsCount = MiniJson.GetInt(root, "modsCount", 0),
            };
        }

        /// <summary>Adds one page of mods to the snapshot; returns the total the API reports for the playset.</summary>
        public static int AddMods(PlaysetSnapshot snapshot, string json)
        {
            object root = MiniJson.Parse(json);
            List<object> mods = MiniJson.AsArray(MiniJson.Get(root, "mods"));
            if (mods == null)
            {
                throw new FormatException("Playset content has no mods array");
            }

            foreach (object mod in mods)
            {
                if (!MiniJson.GetBool(mod, "enabled", true))
                {
                    continue;
                }

                int id = MiniJson.GetInt(mod, "modId", 0);
                string name = MiniJson.GetString(mod, "displayName", string.Empty).Trim();
                if (id <= 0 || string.Equals(name, OwnModName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string version = MiniJson.GetString(mod, "latestVersion", "0").Trim();
                string entry = Describe(name, id, version);
                if (!snapshot.Mods.Contains(entry))
                {
                    snapshot.Mods.Add(entry);
                }
            }

            return MiniJson.GetInt(root, "count", snapshot.Mods.Count);
        }

        /// <summary>The same entry shape the game builds: "Name [pdx ID vN]".</summary>
        public static string Describe(string name, int modId, string version)
        {
            string clean = (name ?? string.Empty).Replace('[', '(').Replace(']', ')').Trim();
            if (clean.Length == 0)
            {
                clean = "Mod " + modId;
            }

            return clean + " [pdx " + modId + " v" + (string.IsNullOrEmpty(version) ? "0" : version) + "]";
        }

        /// <summary>Paradox ids present in <paramref name="after"/> but not <paramref name="before"/> (by id, versions ignored), as display names.</summary>
        public static List<string> AddedNames(IEnumerable<string> before, IEnumerable<string> after)
        {
            var beforeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (string entry in before ?? new string[0])
            {
                string id = ModListCompare.PdxId(entry);
                if (id != null)
                {
                    beforeIds.Add(id);
                }
            }

            var names = new List<string>();
            foreach (string entry in after ?? new string[0])
            {
                string id = ModListCompare.PdxId(entry);
                if (id != null && !beforeIds.Contains(id))
                {
                    names.Add(ModListCompare.DisplayName(entry));
                }
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
    }
}
