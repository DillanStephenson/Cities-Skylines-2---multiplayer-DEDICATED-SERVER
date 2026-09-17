using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Multiplayer.Core.Session;

namespace Multiplayer.Server
{
    /// <summary>
    /// Keeps the world and the reference mod list on disk so a restarted window still has them.
    /// Layout in the data directory: world.bin (the save package bytes), world.meta (key=value lines), mods.txt.
    /// Writes go to a temp file first, then rename, so a crash mid-write never leaves a torn world.
    /// </summary>
    internal sealed class WorldStore
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        private readonly string _directory;

        public string Directory => _directory;

        public string WorldPath => Path.Combine(_directory, "world.bin");

        public string MetaPath => Path.Combine(_directory, "world.meta");

        public string ModsPath => Path.Combine(_directory, "mods.txt");

        public WorldStore(string directory)
        {
            _directory = directory;
            System.IO.Directory.CreateDirectory(directory);
        }

        public WorldSnapshot LoadWorld(ISessionLog log)
        {
            try
            {
                if (!File.Exists(WorldPath) || !File.Exists(MetaPath))
                {
                    return null;
                }

                var info = new WorldInfo();
                foreach (string rawLine in File.ReadAllLines(MetaPath, Encoding.UTF8))
                {
                    string line = rawLine.TrimStart('﻿');
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "Revision": int.TryParse(value, out info.Revision); break;
                        case "Size": long.TryParse(value, out info.Size); break;
                        case "Sha256": info.Sha256 = value; break;
                        case "Guid": info.Guid = value; break;
                        case "SaveName": info.SaveName = value; break;
                        case "CityName": info.CityName = value; break;
                        case "SavedAtUnix": long.TryParse(value, out info.SavedAtUnix); break;
                        case "UploaderName": info.UploaderName = value; break;
                    }
                }

                byte[] data = File.ReadAllBytes(WorldPath);
                if (data.Length != info.Size || !string.Equals(WorldHash.Sha256Hex(data), info.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    log.Warn("Stored world does not match its metadata; ignoring it");
                    return null;
                }

                log.Info("Loaded stored world " + info + " from " + _directory);
                return new WorldSnapshot(info, data);
            }
            catch (Exception ex)
            {
                log.Warn("Could not read the stored world: " + ex.Message);
                return null;
            }
        }

        public void SaveWorld(WorldSnapshot snapshot, ISessionLog log)
        {
            try
            {
                string tmp = WorldPath + ".tmp";
                File.WriteAllBytes(tmp, snapshot.Data);
                Replace(tmp, WorldPath);

                WorldInfo info = snapshot.Info;
                var lines = new[]
                {
                    "Revision=" + info.Revision,
                    "Size=" + info.Size,
                    "Sha256=" + info.Sha256,
                    "Guid=" + info.Guid,
                    "SaveName=" + info.SaveName,
                    "CityName=" + info.CityName,
                    "SavedAtUnix=" + info.SavedAtUnix,
                    "UploaderName=" + info.UploaderName,
                };
                string metaTmp = MetaPath + ".tmp";
                File.WriteAllLines(metaTmp, lines, Utf8NoBom);
                Replace(metaTmp, MetaPath);
                log.Info("Stored world " + info + " in " + _directory);
            }
            catch (Exception ex)
            {
                log.Error("Could not store the world: " + ex.Message);
            }
        }

        private const string PlaysetHeader = "# playset: ";

        /// <summary>The stored mod list, or null when none; the owner's playset name comes back separately.</summary>
        public List<string> LoadMods(out string playset)
        {
            playset = string.Empty;
            try
            {
                if (!File.Exists(ModsPath))
                {
                    return null;
                }

                var mods = new List<string>();
                foreach (string line in File.ReadAllLines(ModsPath, Encoding.UTF8))
                {
                    string clean = line.TrimStart('﻿').Trim();
                    if (clean.StartsWith(PlaysetHeader, StringComparison.Ordinal))
                    {
                        playset = clean.Substring(PlaysetHeader.Length).Trim();
                    }
                    else if (clean.Length > 0 && !clean.StartsWith("#", StringComparison.Ordinal))
                    {
                        mods.Add(clean);
                    }
                }

                return mods;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void SaveMods(IReadOnlyList<string> mods, string playset, ISessionLog log)
        {
            try
            {
                var lines = new List<string>();
                if (!string.IsNullOrEmpty(playset))
                {
                    lines.Add(PlaysetHeader + playset);
                }

                lines.AddRange(mods);
                string tmp = ModsPath + ".tmp";
                File.WriteAllLines(tmp, lines, Utf8NoBom);
                Replace(tmp, ModsPath);
            }
            catch (Exception ex)
            {
                log.Warn("Could not store the mod list: " + ex.Message);
            }
        }

        private static void Replace(string source, string destination)
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            File.Move(source, destination);
        }
    }
}
