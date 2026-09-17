using System;
using System.Collections.Generic;
using System.IO;
using Game.SceneFlow;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Multiplayer
{
    /// <summary>
    /// What this game instance tells the server it runs, so players with different content are kept apart.
    /// Built from the active Paradox playset (which lists asset packs as well as code mods, both of which a
    /// shared city can depend on) plus any locally installed code mods. Entries look like
    /// "Traffic Tool Essentials [pdx 125342 v5]" or "Road Builder Dev [local]"; the server compares them and
    /// can tell a version difference from a missing mod. This mod itself is left out: everyone connected has it.
    /// </summary>
    internal static class ModList
    {
        private const string PlaysetFile = ".cache/Mods/playset_config.json";
        private const string PdxModsFolder = ".cache/Mods/pdx_mods";

        public static List<string> Current()
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            string root = Application.persistentDataPath;

            bool playsetRead = false;
            try
            {
                foreach (string id in EnabledPlaysetIds(Path.Combine(root, PlaysetFile)))
                {
                    set.Add(DescribePdxMod(Path.Combine(root, PdxModsFolder), id));
                }

                playsetRead = true;
            }
            catch (Exception ex)
            {
                Mod.log.Warn("Could not read the active playset (" + ex.Message + "); falling back to loaded code mods");
            }

            try
            {
                foreach (string entry in LoadedCodeMods(playsetRead))
                {
                    set.Add(entry);
                }
            }
            catch (Exception ex)
            {
                Mod.log.Warn("Could not list loaded mods: " + ex.Message);
            }

            return new List<string>(set);
        }

        /// <summary>Display name of the active Paradox playset ("Shared Mods"), or empty when unknown.</summary>
        public static string ActivePlaysetName()
        {
            try
            {
                string path = Path.Combine(Application.persistentDataPath, PlaysetFile);
                if (!File.Exists(path))
                {
                    return string.Empty;
                }

                JObject config = JObject.Parse(File.ReadAllText(path));
                string activeId = (string)config["activePlaysetId"];
                JArray playsets = config["playsets"] as JArray;
                if (activeId == null || playsets == null)
                {
                    return string.Empty;
                }

                foreach (JToken playset in playsets)
                {
                    if (string.Equals((string)playset["id"], activeId, StringComparison.OrdinalIgnoreCase))
                    {
                        return ((string)playset["presentation"]?["name"] ?? string.Empty).Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                Mod.log.Warn("Could not read the active playset name: " + ex.Message);
            }

            return string.Empty;
        }

        /// <summary>Paradox mod ids enabled in the active playset.</summary>
        private static IEnumerable<string> EnabledPlaysetIds(string playsetPath)
        {
            var ids = new List<string>();
            if (!File.Exists(playsetPath))
            {
                throw new FileNotFoundException("playset config missing", playsetPath);
            }

            JObject config = JObject.Parse(File.ReadAllText(playsetPath));
            string activeId = (string)config["activePlaysetId"];
            JArray playsets = config["playsets"] as JArray;
            if (activeId == null || playsets == null)
            {
                throw new InvalidDataException("playset config has no active playset");
            }

            foreach (JToken playset in playsets)
            {
                if (!string.Equals((string)playset["id"], activeId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                JArray mods = playset["mods"] as JArray;
                if (mods == null)
                {
                    break;
                }

                foreach (JToken mod in mods)
                {
                    if (string.Equals((string)mod["source"], "pdx_mods", StringComparison.OrdinalIgnoreCase)
                        && (bool?)mod["isEnabled"] == true)
                    {
                        string id = (string)mod["sourceId"];
                        if (!string.IsNullOrEmpty(id))
                        {
                            ids.Add(id);
                        }
                    }
                }

                break;
            }

            return ids;
        }

        /// <summary>"Name [pdx ID vN]" from the installed folder pdx_mods/ID_N; the name comes from the files inside.</summary>
        private static string DescribePdxMod(string pdxModsFolder, string id)
        {
            string bestFolder = null;
            int bestVersion = -1;
            if (Directory.Exists(pdxModsFolder))
            {
                foreach (string folder in Directory.GetDirectories(pdxModsFolder, id + "_*"))
                {
                    string suffix = Path.GetFileName(folder).Substring(id.Length + 1);
                    if (int.TryParse(suffix, out int version) && version > bestVersion)
                    {
                        bestVersion = version;
                        bestFolder = folder;
                    }
                }
            }

            string name = bestFolder != null ? GuessName(bestFolder) : null;
            string versionText = bestVersion >= 0 ? "v" + bestVersion : "v?";
            return (string.IsNullOrEmpty(name) ? "pdx " + id : name) + " [pdx " + id + " " + versionText + "]";
        }

        private static string GuessName(string folder)
        {
            try
            {
                string[] dlls = Directory.GetFiles(folder, "*.dll");
                foreach (string dll in dlls)
                {
                    string name = Path.GetFileNameWithoutExtension(dll);
                    if (!name.EndsWith("_win_x86_64", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".Steam", StringComparison.OrdinalIgnoreCase))
                    {
                        return name;
                    }
                }

                string[] packages = Directory.GetFiles(folder, "*.cok");
                if (packages.Length > 0)
                {
                    string name = Path.GetFileNameWithoutExtension(packages[0]);
                    int underscore = name.LastIndexOf('_');
                    return underscore > 0 && name.Length - underscore == 33 ? name.Substring(0, underscore) : name;
                }
            }
            catch (Exception)
            {
            }

            return null;
        }

        /// <summary>Code mods the game actually loaded. With a playset read, only local (non-Paradox) ones are added.</summary>
        private static IEnumerable<string> LoadedCodeMods(bool onlyLocal)
        {
            var entries = new List<string>();
            var manager = GameManager.instance?.modManager;
            if (manager == null)
            {
                return entries;
            }

            foreach (var info in manager)
            {
                if (info == null || !info.isLoaded || info.asset == null)
                {
                    continue;
                }

                string path = (info.asset.path ?? string.Empty).Replace('\\', '/');
                bool fromPdx = path.IndexOf("/pdx_mods/", StringComparison.OrdinalIgnoreCase) >= 0;
                if (onlyLocal && fromPdx)
                {
                    continue;
                }

                string name = Clean(info.asset.name ?? info.name);
                if (name.Length == 0 || string.Equals(name, Mod.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                entries.Add(name + (fromPdx ? " [pdx]" : " [local]"));
            }

            return entries;
        }

        public static string Clean(string mod)
        {
            string text = (mod ?? string.Empty).Trim();
            int comma = text.IndexOf(',');
            if (comma > 0)
            {
                text = text.Substring(0, comma).Trim();
            }

            return text;
        }
    }
}
