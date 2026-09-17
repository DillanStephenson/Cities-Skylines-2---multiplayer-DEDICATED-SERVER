using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Multiplayer.Core.Protocol;
using Multiplayer.Core.Wire;

namespace Multiplayer.Core.Session
{
    /// <summary>Everything known about the world the server holds, apart from the bytes themselves.</summary>
    public sealed class WorldInfo
    {
        /// <summary>Counts up on every accepted upload; clients compare it to what they last loaded.</summary>
        public int Revision;

        public long Size;

        /// <summary>Lower-case hex SHA-256 of the bytes.</summary>
        public string Sha256 = string.Empty;

        /// <summary>Asset guid the game gave the save package, so every player registers it under the same identity.</summary>
        public string Guid = string.Empty;

        /// <summary>Name of the save package without extension (also the name in the game's Load menu).</summary>
        public string SaveName = string.Empty;

        public string CityName = string.Empty;

        public long SavedAtUnix;

        public string UploaderName = string.Empty;

        public WorldInfo Clone()
        {
            return (WorldInfo)MemberwiseClone();
        }

        public void Write(BinaryWriter writer)
        {
            writer.Write(Revision);
            writer.Write(Size);
            writer.WriteText(Sha256);
            writer.WriteText(Guid);
            writer.WriteText(SaveName);
            writer.WriteText(CityName);
            writer.Write(SavedAtUnix);
            writer.WriteText(UploaderName);
        }

        public void Read(BinaryReader reader)
        {
            Revision = reader.ReadInt32();
            Size = reader.ReadInt64();
            Sha256 = reader.ReadText();
            Guid = reader.ReadText();
            SaveName = reader.ReadText();
            CityName = reader.ReadText();
            SavedAtUnix = reader.ReadInt64();
            UploaderName = reader.ReadText();
        }

        public override string ToString()
        {
            return (string.IsNullOrEmpty(CityName) ? SaveName : CityName) + " rev " + Revision + " (" + FormatSize(Size) + ")";
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024 * 1024)
            {
                return (bytes / 1024.0).ToString("0.0") + " KB";
            }

            return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
        }
    }

    /// <summary>A world plus its bytes: what the server keeps and what a joiner receives.</summary>
    public sealed class WorldSnapshot
    {
        public WorldInfo Info;
        public byte[] Data;

        public WorldSnapshot(WorldInfo info, byte[] data)
        {
            Info = info ?? throw new ArgumentNullException(nameof(info));
            Data = data ?? throw new ArgumentNullException(nameof(data));
        }
    }

    public static class WorldHash
    {
        public static string Sha256Hex(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(data ?? new byte[0]);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                {
                    builder.Append(b.ToString("x2"));
                }

                return builder.ToString();
            }
        }
    }

    /// <summary>
    /// Compares two players' mod lists and words the difference for a rejection message.
    /// Entries are free text, but the game side writes Paradox mods as "Name [pdx ID vN]" and local
    /// mods as "Name [local]", which lets the comparison say "version differs" instead of missing plus extra.
    /// </summary>
    public static class ModListCompare
    {
        public static List<string> Normalize(IEnumerable<string> mods)
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (mods != null)
            {
                foreach (string mod in mods)
                {
                    string trimmed = (mod ?? string.Empty).Trim();
                    if (trimmed.Length > 0)
                    {
                        set.Add(trimmed);
                    }
                }
            }

            return new List<string>(set);
        }

        /// <summary>
        /// Null when the lists match; otherwise a human-readable reason naming what differs. With
        /// <paramref name="ignoreVersions"/> the same Paradox mod at another version counts as a match, which is
        /// the everyday case: a shared playset updates on each PC at its own pace.
        /// </summary>
        public static string Describe(IList<string> reference, IList<string> candidate, string ownName, bool ignoreVersions = false)
        {
            var referenceSet = new HashSet<string>(reference ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var candidateSet = new HashSet<string>(candidate ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            var missing = new List<string>();
            var extra = new List<string>();
            foreach (string mod in referenceSet)
            {
                if (!candidateSet.Contains(mod) && !IsOwn(mod, ownName))
                {
                    missing.Add(mod);
                }
            }

            foreach (string mod in candidateSet)
            {
                if (!referenceSet.Contains(mod) && !IsOwn(mod, ownName))
                {
                    extra.Add(mod);
                }
            }

            if (missing.Count == 0 && extra.Count == 0)
            {
                return null;
            }

            // Pair up entries that are the same Paradox mod at different versions.
            var versionDiffers = new List<string>();
            var missingById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string mod in missing)
            {
                string id = PdxId(mod);
                if (id != null && !missingById.ContainsKey(id))
                {
                    missingById[id] = mod;
                }
            }

            var stillMissing = new HashSet<string>(missing, StringComparer.OrdinalIgnoreCase);
            var stillExtra = new List<string>();
            foreach (string mod in extra)
            {
                string id = PdxId(mod);
                if (id != null && missingById.TryGetValue(id, out string hostEntry))
                {
                    if (!ignoreVersions)
                    {
                        versionDiffers.Add(DisplayName(hostEntry) + " (host " + PdxVersion(hostEntry) + ", you " + PdxVersion(mod) + ")");
                    }

                    stillMissing.Remove(hostEntry);
                }
                else
                {
                    stillExtra.Add(mod);
                }
            }

            var missingList = new List<string>(stillMissing);
            if (versionDiffers.Count == 0 && missingList.Count == 0 && stillExtra.Count == 0)
            {
                return null;
            }

            missingList.Sort(StringComparer.OrdinalIgnoreCase);
            stillExtra.Sort(StringComparer.OrdinalIgnoreCase);
            versionDiffers.Sort(StringComparer.OrdinalIgnoreCase);

            var builder = new StringBuilder("Mods differ from the host's");
            if (versionDiffers.Count > 0)
            {
                builder.Append("; version differs: ").Append(Join(versionDiffers, 6));
            }

            if (missingList.Count > 0)
            {
                builder.Append("; missing: ").Append(Join(missingList, 6));
            }

            if (stillExtra.Count > 0)
            {
                builder.Append("; extra: ").Append(Join(stillExtra, 6));
            }

            return builder.ToString();
        }

        /// <summary>"Traffic Tool Essentials [pdx 125342 v5]" -> "125342"; null for anything else.</summary>
        public static string PdxId(string entry)
        {
            int open = (entry ?? string.Empty).LastIndexOf("[pdx ", StringComparison.OrdinalIgnoreCase);
            if (open < 0)
            {
                return null;
            }

            string inner = entry.Substring(open + 5).TrimEnd(']');
            int space = inner.IndexOf(' ');
            return space > 0 ? inner.Substring(0, space) : inner;
        }

        public static string PdxVersion(string entry)
        {
            int v = (entry ?? string.Empty).LastIndexOf(" v", StringComparison.OrdinalIgnoreCase);
            int close = (entry ?? string.Empty).LastIndexOf(']');
            return v > 0 && close > v ? entry.Substring(v + 1, close - v - 1) : "?";
        }

        /// <summary>Strip the bracketed source tag so messages read naturally.</summary>
        public static string DisplayName(string entry)
        {
            int open = (entry ?? string.Empty).LastIndexOf('[');
            return open > 0 ? entry.Substring(0, open).Trim() : (entry ?? string.Empty);
        }

        private static bool IsOwn(string mod, string ownName)
        {
            return !string.IsNullOrEmpty(ownName) && string.Equals(mod, ownName, StringComparison.OrdinalIgnoreCase);
        }

        private static string Join(List<string> items, int limit)
        {
            if (items.Count <= limit)
            {
                return string.Join(", ", items.ToArray());
            }

            return string.Join(", ", items.GetRange(0, limit).ToArray()) + " and " + (items.Count - limit) + " more";
        }
    }
}
