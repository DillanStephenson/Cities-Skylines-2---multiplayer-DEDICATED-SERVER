using System;
using System.IO;
using UnityEngine;

namespace Multiplayer
{
    /// <summary>
    /// Developer hooks so a session can be started without clicking through the options UI, which makes
    /// headless smoke tests possible (game hosts, a console client joins). Both files live next to the
    /// mod's settings file and are ignored when absent:
    ///   ModsSettings/Multiplayer/dev-autohost.txt   contains a port (or is empty for the configured one)
    ///   ModsSettings/Multiplayer/dev-autojoin.txt   line 1 address[:port], line 2 password, line 3 owner key (lines 2-3 optional)
    /// </summary>
    internal static class DevTriggers
    {
        public const string AutoHostFile = "dev-autohost.txt";
        public const string AutoJoinFile = "dev-autojoin.txt";

        /// <summary>Once in a city and connected, build a short test road through the replay engine and broadcast it.</summary>
        public const string BuildTestFile = "dev-build.txt";

        public static void Apply(MultiplayerService service, Setting settings)
        {
            if (service == null || settings == null)
            {
                return;
            }

            try
            {
                string directory = Path.Combine(Application.persistentDataPath, "ModsSettings", Mod.Name);
                string autoHost = Path.Combine(directory, AutoHostFile);
                string autoJoin = Path.Combine(directory, AutoJoinFile);

                if (File.Exists(Path.Combine(directory, BuildTestFile)))
                {
                    Mod.log.Info(BuildTestFile + " present: a test road will be built once in a city and connected");
                    service.RequestDevBuild();
                }

                if (File.Exists(autoHost))
                {
                    string port = File.ReadAllText(autoHost).Trim();
                    if (port.Length > 0)
                    {
                        settings.HostPort = port;
                    }

                    Mod.log.Info(AutoHostFile + " present: hosting on port " + settings.HostPort);
                    service.HostGame();
                }
                else if (File.Exists(autoJoin))
                {
                    // Line 1: address[:port]; line 2 (optional): password; line 3 (optional): owner key.
                    string[] lines = File.ReadAllLines(autoJoin);
                    if (lines.Length > 0 && lines[0].Trim().Length > 0)
                    {
                        settings.JoinAddress = lines[0].Trim();
                    }

                    if (lines.Length > 1)
                    {
                        settings.JoinPassword = lines[1].Trim();
                    }

                    if (lines.Length > 2)
                    {
                        settings.JoinOwnerKey = lines[2].Trim();
                    }

                    Mod.log.Info(AutoJoinFile + " present: joining " + settings.JoinAddress + (settings.JoinOwnerKey.Length > 0 ? " as owner" : ""));
                    service.JoinGame();
                }
            }
            catch (Exception ex)
            {
                Mod.log.Warn("Dev trigger failed: " + ex.Message);
            }
        }
    }
}
