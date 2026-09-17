using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Multiplayer.Core.Session;
using Multiplayer.Core.Util;

namespace Multiplayer.Server
{
    /// <summary>
    /// server.json: every setting of a dedicated server in one file. Read at start (before environment
    /// variables and command-line options, which still win) and re-read while running whenever it changes.
    /// </summary>
    internal static class ServerConfigFile
    {
        public const string DefaultName = "server.json";

        /// <summary>Applies the file's keys onto <paramref name="options"/>. Unknown keys are reported, not fatal.</summary>
        public static void Apply(string json, ServerOptions options, List<string> warnings)
        {
            object root = MiniJson.Parse(json);
            Dictionary<string, object> map = MiniJson.AsObject(root);
            if (map == null)
            {
                throw new FormatException("server.json must contain one JSON object");
            }

            foreach (KeyValuePair<string, object> pair in map)
            {
                string key = pair.Key.Trim();
                object value = pair.Value;
                switch (key.ToLowerInvariant())
                {
                    case "name": options.ServerName = Text(value, options.ServerName); break;
                    case "port": options.Port = Number(value, options.Port, 1, ushort.MaxValue, key, warnings); break;
                    case "password": options.Password = Text(value, string.Empty); break;
                    case "ownerkey": options.OwnerKey = Text(value, string.Empty); break;
                    case "gameversion": options.GameVersion = Text(value, string.Empty); break;
                    case "maxplayers": options.MaxPlayers = Number(value, options.MaxPlayers, 1, Multiplayer.Core.Protocol.ProtocolConstants.MaxPlayers, key, warnings); break;
                    case "modcheck": options.ModCheck = ServerOptions.NormalizeModCheck(Text(value, "names")); break;
                    case "playsetid": options.PlaysetId = Number(value, 0, 0, int.MaxValue, key, warnings); break;
                    case "playsethint": options.PlaysetHint = Text(value, string.Empty).Trim(); break;
                    case "playsetpollminutes": options.PlaysetPollMinutes = Number(value, options.PlaysetPollMinutes, 1, 1440, key, warnings); break;
                    case "updatecheck": options.UpdateCheck = Flag(value, options.UpdateCheck); break;
                    case "updaterepository": options.UpdateRepository = Text(value, options.UpdateRepository).Trim(); break;
                    case "datadir": options.DataDirectory = Text(value, options.DataDirectory); break;
                    case "welcome": options.Welcome = Text(value, string.Empty).Trim(); break;
                    case "requiredmods":
                        options.RequiredMods = Mods(value, warnings);
                        break;
                    case "acceptmodversions":
                        options.AcceptModVersions = Strings(value, warnings, key);
                        break;
                    default:
                        if (!key.StartsWith("_", StringComparison.Ordinal))
                        {
                            warnings.Add("server.json: unknown setting '" + key + "' ignored");
                        }

                        break;
                }
            }
        }

        /// <summary>Reads and applies the file; a missing file is fine, a broken one is not.</summary>
        public static bool Load(string path, ServerOptions options, List<string> warnings)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return false;
            }

            Apply(File.ReadAllText(path, Encoding.UTF8), options, warnings);
            return true;
        }

        /// <summary>
        /// "125342" becomes "Mod 125342 [pdx 125342 v0]", entries already in the "Name [pdx ID vN]" or
        /// "Name [local]" shape stay, anything else counts as a local mod by that name.
        /// </summary>
        public static string NormalizeModEntry(string entry)
        {
            return ModListCompare.NormalizeEntry(entry);
        }

        /// <summary>A starting file with every key and a line of help for each.</summary>
        public static string Template(string name, string password, string ownerKey, string gameVersion)
        {
            var b = new StringBuilder();
            b.Append("{\n");
            b.Append("  \"_help\": \"Settings of the multiplayer server. Edit and save; the server picks changes up within ten seconds. port, ownerKey, gameVersion and dataDir need a restart.\",\n");
            b.Append("  \"name\": ").Append(Quote(name)).Append(",\n");
            b.Append("  \"port\": 27015,\n");
            b.Append("  \"password\": ").Append(Quote(password)).Append(",\n");
            b.Append("  \"ownerKey\": ").Append(Quote(ownerKey)).Append(",\n");
            b.Append("  \"_ownerKey\": \"Whoever enters this under Join > Owner key is the host. Keep it out of the group chat.\",\n");
            b.Append("  \"gameVersion\": ").Append(Quote(gameVersion)).Append(",\n");
            b.Append("  \"_gameVersion\": \"Players must run exactly this game build (Logs\\\\Multiplayer.log on a PC prints it). Empty = not checked.\",\n");
            b.Append("  \"maxPlayers\": 8,\n");
            b.Append("  \"modCheck\": \"names\",\n");
            b.Append("  \"_modCheck\": \"names = same mods as the host at any version. strict = same versions too. off = no check.\",\n");
            b.Append("  \"playsetId\": 0,\n");
            b.Append("  \"_playsetId\": \"Public Paradox playset to follow (the number in its URL). Its published mod list becomes the required list, re-read every few minutes. 0 = off.\",\n");
            b.Append("  \"playsetHint\": \"\",\n");
            b.Append("  \"_playsetHint\": \"Extra text shown to rejected players, for example where to find the playset.\",\n");
            b.Append("  \"requiredMods\": [],\n");
            b.Append("  \"_requiredMods\": \"Fixed list instead of a playset: Paradox mod ids as strings (\\\"125342\\\") or names for local mods. Empty = learn the list from the host when they join.\",\n");
            b.Append("  \"welcome\": \"\",\n");
            b.Append("  \"_welcome\": \"Chat message sent to every player who joins. Empty = none.\",\n");
            b.Append("  \"updateCheck\": true\n");
            b.Append("}\n");
            return b.ToString();
        }

        private static string Text(object value, string fallback)
        {
            if (value == null)
            {
                return fallback;
            }

            if (value is string s)
            {
                return s;
            }

            if (value is double d)
            {
                return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (value is bool b)
            {
                return b ? "true" : "false";
            }

            return fallback;
        }

        private static int Number(object value, int fallback, int min, int max, string key, List<string> warnings)
        {
            int number;
            if (value is double d)
            {
                number = (int)Math.Round(d);
            }
            else if (value is string s && int.TryParse(s.Trim(), out int parsed))
            {
                number = parsed;
            }
            else
            {
                warnings.Add("server.json: '" + key + "' should be a number; using " + fallback);
                return fallback;
            }

            if (number < min || number > max)
            {
                warnings.Add("server.json: '" + key + "' must be " + min + ".." + max + "; using " + fallback);
                return fallback;
            }

            return number;
        }

        private static bool Flag(object value, bool fallback)
        {
            if (value is bool b)
            {
                return b;
            }

            if (value is string s)
            {
                return ServerOptions.NormalizeModCheck(s) != "off" && !string.Equals(s.Trim(), "off", StringComparison.OrdinalIgnoreCase);
            }

            return fallback;
        }

        private static List<string> Mods(object value, List<string> warnings)
        {
            var result = new List<string>();
            List<object> items = MiniJson.AsArray(value);
            if (items == null)
            {
                if (value is string single && single.Trim().Length > 0)
                {
                    foreach (string part in single.Split(','))
                    {
                        string entry = NormalizeModEntry(part);
                        if (entry.Length > 0)
                        {
                            result.Add(entry);
                        }
                    }

                    return result;
                }

                if (value != null)
                {
                    warnings.Add("server.json: 'requiredMods' should be a list of strings");
                }

                return result;
            }

            foreach (object item in items)
            {
                string entry = NormalizeModEntry(Text(item, string.Empty));
                if (entry.Length > 0)
                {
                    result.Add(entry);
                }
            }

            return result;
        }

        private static List<string> Strings(object value, List<string> warnings, string key)
        {
            var result = new List<string>();
            List<object> items = MiniJson.AsArray(value);
            if (items == null)
            {
                if (value is string single)
                {
                    foreach (string part in single.Split(','))
                    {
                        if (part.Trim().Length > 0)
                        {
                            result.Add(part.Trim());
                        }
                    }
                }
                else if (value != null)
                {
                    warnings.Add("server.json: '" + key + "' should be a list of strings");
                }

                return result;
            }

            foreach (object item in items)
            {
                string text = Text(item, string.Empty).Trim();
                if (text.Length > 0)
                {
                    result.Add(text);
                }
            }

            return result;
        }

        private static string Quote(string text)
        {
            var b = new StringBuilder("\"");
            foreach (char c in text ?? string.Empty)
            {
                if (c == '"' || c == '\\')
                {
                    b.Append('\\').Append(c);
                }
                else if (c < ' ')
                {
                    b.Append(' ');
                }
                else
                {
                    b.Append(c);
                }
            }

            return b.Append('"').ToString();
        }
    }
}
