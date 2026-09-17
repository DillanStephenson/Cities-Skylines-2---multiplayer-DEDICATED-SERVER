using System;
using System.Text;
using Multiplayer.Core.Protocol;

namespace Multiplayer.Server
{
    /// <summary>Command line of the server window. Every option has a default so `Multiplayer.Server.exe` alone works.</summary>
    internal sealed class ServerOptions
    {
        public int Port = ProtocolConstants.DefaultPort;
        public string Password = string.Empty;
        public string OwnerKey = string.Empty;
        public bool OwnerKeyGenerated;
        public string GameVersion = string.Empty;
        public string ServerName = "Server";
        public int MaxPlayers = 8;

        /// <summary>Exit when this process is gone (the game that spawned us).</summary>
        public int ParentPid;

        /// <summary>Exit once the owner has been gone for <see cref="OwnerGraceSeconds"/>.</summary>
        public bool ExitWhenOwnerLeaves;
        public int OwnerGraceSeconds = 15;

        /// <summary>Line-by-line output instead of the status panel (also forced when output is redirected).</summary>
        public bool Plain;

        /// <summary>Let players join with mods that differ from the host's.</summary>
        /// <summary>strict = same mods and versions as the host; names = same mods, any version (default); off = no check.</summary>
        public string ModCheck = "names";

        /// <summary>Text added to mod rejections, typically the public playset id everyone should activate.</summary>
        public string PlaysetHint = string.Empty;

        /// <summary>Public Paradox playset to follow: its published mod list becomes the reference, re-read every few minutes. 0 = none.</summary>
        public int PlaysetId;

        /// <summary>How often the linked playset is checked for a new public version.</summary>
        public int PlaysetPollMinutes = 5;

        /// <summary>Ask GitHub for the newest release at start and every few hours; off with --no-update-check or CS2MP_UPDATE_CHECK=off.</summary>
        public bool UpdateCheck = true;

        /// <summary>GitHub repository whose releases are checked (owner/name).</summary>
        public string UpdateRepository = Multiplayer.Core.Session.GitHubReleases.Repository;

        /// <summary>Where the world and mod list are kept between runs. Default: a "world" folder next to the executable.</summary>
        public string DataDirectory = string.Empty;

        /// <summary>Secrets may come from the environment so they never appear in a process list; arguments override.</summary>
        public const string PasswordVariable = "CS2MP_PASSWORD";
        public const string OwnerKeyVariable = "CS2MP_OWNER_KEY";
        public const string ModCheckVariable = "CS2MP_MOD_CHECK";
        public const string PlaysetVariable = "CS2MP_PLAYSET";
        public const string PlaysetIdVariable = "CS2MP_PLAYSET_ID";
        public const string UpdateCheckVariable = "CS2MP_UPDATE_CHECK";

        public static ServerOptions Parse(string[] args)
        {
            var options = new ServerOptions
            {
                Password = Environment.GetEnvironmentVariable(PasswordVariable) ?? string.Empty,
                OwnerKey = Environment.GetEnvironmentVariable(OwnerKeyVariable) ?? string.Empty,
                ModCheck = NormalizeModCheck(Environment.GetEnvironmentVariable(ModCheckVariable)),
                PlaysetHint = (Environment.GetEnvironmentVariable(PlaysetVariable) ?? string.Empty).Trim(),
                PlaysetId = ParseOptionalInt(Environment.GetEnvironmentVariable(PlaysetIdVariable)),
                UpdateCheck = NormalizeModCheck(Environment.GetEnvironmentVariable(UpdateCheckVariable) ?? "on") != "off",
            };

            for (int i = 0; i < args.Length; i++)
            {
                string key = args[i].ToLowerInvariant();
                switch (key)
                {
                    case "--port": options.Port = ParseInt(Value(args, ref i), "port", 1, ushort.MaxValue); break;
                    case "--password": options.Password = Value(args, ref i); break;
                    case "--password-base64": options.Password = FromBase64(Value(args, ref i)); break;
                    case "--owner-key": options.OwnerKey = Value(args, ref i); break;
                    case "--owner-key-base64": options.OwnerKey = FromBase64(Value(args, ref i)); break;
                    case "--game-version": options.GameVersion = Value(args, ref i); break;
                    case "--name": options.ServerName = Value(args, ref i); break;
                    case "--max-players": options.MaxPlayers = ParseInt(Value(args, ref i), "max-players", 1, ProtocolConstants.MaxPlayers); break;
                    case "--parent-pid": options.ParentPid = ParseInt(Value(args, ref i), "parent-pid", 1, int.MaxValue); break;
                    case "--owner-grace": options.OwnerGraceSeconds = ParseInt(Value(args, ref i), "owner-grace", 0, 3600); break;
                    case "--exit-when-owner-leaves": options.ExitWhenOwnerLeaves = true; break;
                    case "--plain": options.Plain = true; break;
                    case "--no-mod-check": options.ModCheck = "off"; break;
                    case "--mod-check": options.ModCheck = NormalizeModCheck(Value(args, ref i)); break;
                    case "--playset": options.PlaysetHint = Value(args, ref i).Trim(); break;
                    case "--playset-id": options.PlaysetId = ParseInt(Value(args, ref i), "playset-id", 1, int.MaxValue); break;
                    case "--playset-poll": options.PlaysetPollMinutes = ParseInt(Value(args, ref i), "playset-poll", 1, 1440); break;
                    case "--no-update-check": options.UpdateCheck = false; break;
                    case "--update-repo": options.UpdateRepository = Value(args, ref i).Trim(); break;
                    case "--data-dir": options.DataDirectory = Value(args, ref i); break;
                    case "-h":
                    case "--help":
                        throw new ArgumentException(Usage());
                    default:
                        throw new ArgumentException("Unknown option " + args[i] + "\n" + Usage());
                }
            }

            if (string.IsNullOrWhiteSpace(options.ServerName))
            {
                options.ServerName = "Server";
            }

            if (string.IsNullOrEmpty(options.OwnerKey))
            {
                options.OwnerKey = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();
                options.OwnerKeyGenerated = true;
            }

            return options;
        }

        public static string Usage()
        {
            return "Multiplayer.Server [--port N] [--password P | --password-base64 B] [--owner-key K | --owner-key-base64 B]\n" +
                   "                   [--game-version V] [--name NAME] [--max-players N] [--parent-pid PID]\n" +
                   "                   [--exit-when-owner-leaves] [--owner-grace SECONDS] [--plain] [--mod-check strict|names|off] [--playset TEXT] [--playset-id N] [--playset-poll MIN] [--no-update-check] [--data-dir DIR]\n" +
                   "Environment " + PasswordVariable + " and " + OwnerKeyVariable + " set the secrets without showing them in the process list.\n" +
                   "Without an owner key one is generated and shown; enter it in the game (Join > Owner key) to be the host.";
        }

        /// <summary>The value after an option; another option or the end of the line means it is missing.</summary>
        private static string Value(string[] args, ref int i)
        {
            string option = args[i];
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Option " + option + " needs a value\n" + Usage());
            }

            i++;
            return args[i] ?? string.Empty;
        }

        private static int ParseInt(string text, string name, int min, int max)
        {
            if (!int.TryParse(text, out int value) || value < min || value > max)
            {
                throw new ArgumentException("Invalid " + name + " '" + text + "' (expected " + min + ".." + max + ")");
            }

            return value;
        }

        private static string FromBase64(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(text));
            }
            catch (FormatException)
            {
                throw new ArgumentException("Not valid base64: " + text);
            }
        }

        /// <summary>A positive integer from the environment, or 0 when unset or unusable.</summary>
        public static int ParseOptionalInt(string value)
        {
            return int.TryParse((value ?? string.Empty).Trim(), out int parsed) && parsed > 0 ? parsed : 0;
        }

        /// <summary>"strict", "names" or "off"; anything else (including nothing) means "names".</summary>
        public static string NormalizeModCheck(string value)
        {
            string mode = (value ?? string.Empty).Trim().ToLowerInvariant();
            switch (mode)
            {
                case "strict":
                case "off":
                case "names":
                    return mode;
                case "none":
                case "false":
                case "0":
                    return "off";
                case "exact":
                case "versions":
                    return "strict";
                default:
                    return "names";
            }
        }

    }
}
