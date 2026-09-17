using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Multiplayer.Core.Session;
using Multiplayer.Core.Transport.Tcp;

namespace Multiplayer.Server
{
    /// <summary>
    /// Entry point of the server window. Owns the <see cref="ServerSession"/>, redraws the status panel,
    /// runs admin commands, and exits when told to, when the spawning game dies, or when the owner is gone.
    /// </summary>
    internal static class Program
    {
        private const int TickMs = 10;
        private const int DrawIntervalMs = 250;

        private static int Main(string[] args)
        {
            FileLog.Open();
            FileLog.Write("Started with: " + string.Join(" ", args));

            ServerOptions options;
            var settingsWarnings = new List<string>();
            try
            {
                options = ServerOptions.Parse(args, settingsWarnings);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is FormatException)
            {
                FileLog.Write("Bad arguments: " + ex.Message);
                Console.Error.WriteLine(ex.Message);
                Console.Error.WriteLine("(closing in 10 s; details also in " + FileLog.Path + ")");
                Thread.Sleep(10000);
                return 2;
            }

            var view = new ConsoleView(options.Plain);
            try
            {
                Console.Title = "Multiplayer server - port " + options.Port;
            }
            catch (Exception)
            {
            }

            if (options.ConfigPath.Length > 0)
            {
                view.Append("Settings from " + options.ConfigPath + " (edit and save; changes apply within ten seconds).");
            }

            foreach (string warning in settingsWarnings)
            {
                view.Append(warning, ConsoleColor.Yellow);
            }

            var config = new ServerConfig
            {
                ServerName = options.ServerName,
                Password = options.Password,
                OwnerKey = options.OwnerKey,
                GameVersion = options.GameVersion,
                MaxPlayers = options.MaxPlayers,
                RequireMatchingMods = options.ModCheck != "off",
                IgnoreModVersions = options.ModCheck != "strict",
                PlaysetHint = options.PlaysetHint,
                ExtraModVersions = new List<string>(options.AcceptModVersions),
                // Only the window the game spawned dies with its game; a standalone server outlives any one player.
                OwnerMayStop = options.ExitWhenOwnerLeaves,
            };
            var session = new ServerSession(config, view);
            string modCheckText = options.ModCheck == "off" ? "Mod check off: anyone with this mod may join."
                : options.ModCheck == "strict" ? "Mod check strict: same mods and versions as the host."
                : "Mod check: same mods as the host, any version.";
            if (options.PlaysetHint.Length > 0)
            {
                modCheckText += " Playset hint for rejected joiners: " + options.PlaysetHint;
            }

            view.Append(modCheckText);

            UpdateWatcher updates = null;
            if (options.UpdateCheck && options.UpdateRepository.Length > 0)
            {
                updates = new UpdateWatcher(options.UpdateRepository, 6, view);
            }

            PlaysetWatcher watcher = null;
            if (options.PlaysetId > 0)
            {
                watcher = new PlaysetWatcher(options.PlaysetId, options.PlaysetPollMinutes, view);
                view.Append("Following Paradox playset " + options.PlaysetId + ": its published mod list is the reference, checked every " + options.PlaysetPollMinutes + " min.", ConsoleColor.Cyan);
            }
            var clock = Stopwatch.StartNew();

            string dataDirectory = string.IsNullOrEmpty(options.DataDirectory)
                ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".", "world")
                : options.DataDirectory;
            WorldStore store = null;
            try
            {
                store = new WorldStore(dataDirectory);
                session.SetWorld(store.LoadWorld(view));
                List<string> storedMods = store.LoadMods(out string storedPlayset);
                if (storedMods != null)
                {
                    session.SetReferenceMods(storedMods, storedPlayset, watcher != null);
                    view.Append("Reference mod list restored: " + storedMods.Count + " mods" + (storedPlayset.Length > 0 ? " (playset '" + storedPlayset + "')" : "") + (watcher != null ? ", until the playset is read" : ""));
                }
            }
            catch (Exception ex)
            {
                view.Append("World storage unavailable (" + ex.Message + "); the world will live in memory only.", ConsoleColor.Yellow);
            }

            if (options.PlaysetId == 0 && options.RequiredMods.Count > 0)
            {
                session.SetReferenceMods(options.RequiredMods, "server.json", locked: true);
                view.Append("Required mods from server.json: " + options.RequiredMods.Count + " (players must run these; the host's own list is not used).", ConsoleColor.Cyan);
            }

            session.WorldChanged += snapshot =>
            {
                view.Append("World updated: " + snapshot.Info + " uploaded by " + snapshot.Info.UploaderName, ConsoleColor.Green);
                store?.SaveWorld(snapshot, view);
            };
            session.WorldSent += (player, info) => view.Append("Sending world " + info + " to " + player, ConsoleColor.Cyan);
            session.ReferenceModsChanged += mods =>
            {
                view.Append("Players must now run the host's " + mods.Count + " mods" + (session.ReferencePlayset.Length > 0 ? " (playset '" + session.ReferencePlayset + "')" : ""), ConsoleColor.Cyan);
                store?.SaveMods(mods, session.ReferencePlayset, view);
            };

            session.PlayerJoined += p => view.Append("+ " + p + " joined (" + session.Players.Count + " online)", ConsoleColor.Green);
            session.PlayerJoined += p =>
            {
                if (options.Welcome.Length > 0)
                {
                    session.Tell(p.PlayerId, options.Welcome);
                }
            };
            session.PlayerLeft += (p, reason) => view.Append("- " + p + " left: " + reason + " (" + session.Players.Count + " online)", ConsoleColor.Yellow);
            session.ChatReceived += (p, text) => view.Append("<" + p.Name + "> " + text, ConsoleColor.White);
            session.SimulationSpeedChanged += (speed, p) => view.Append("speed " + ConsoleView.FormatSpeed(speed) + (p != null ? " requested by " + p.Name : " set from console"), ConsoleColor.Cyan);
            session.GameplayCommandRelayed += c =>
            {
                if (c.Kind == Multiplayer.Core.Build.PresenceCommand.Kind || c.Kind == Multiplayer.Core.Build.PreviewCommand.Kind)
                {
                    // Several a second per player while they pan about or drag a road out; not worth a line each.
                    return;
                }

                string detail = c.Kind + " from player " + c.OriginPlayerId + " (" + c.Payload.Length + " bytes)";
                try
                {
                    if (c.Kind == Multiplayer.Core.Build.BuildCommand.Kind)
                    {
                        detail = Multiplayer.Core.Build.BuildCommand.FromBytes(c.Payload) + " from player " + c.OriginPlayerId;
                    }
                    else if (c.Kind == Multiplayer.Core.Build.ModDataCommand.Kind)
                    {
                        detail = "mod data " + Multiplayer.Core.Build.ModDataCommand.FromBytes(c.Payload) + " from player " + c.OriginPlayerId;
                    }
                    else if (c.Kind == Multiplayer.Core.Build.ModConfigCommand.Kind)
                    {
                        detail = Multiplayer.Core.Build.ModConfigCommand.FromBytes(c.Payload) + " from player " + c.OriginPlayerId;
                    }
                    else if (c.Kind == Multiplayer.Core.Build.LaneConnectionsCommand.Kind)
                    {
                        detail = Multiplayer.Core.Build.LaneConnectionsCommand.FromBytes(c.Payload) + " from player " + c.OriginPlayerId;
                    }
                    else if (c.Kind == Multiplayer.Core.Build.BuildResultCommand.Kind)
                    {
                        detail = "result " + Multiplayer.Core.Build.BuildResultCommand.FromBytes(c.Payload) + " from player " + c.OriginPlayerId;
                    }
                }
                catch (Exception)
                {
                }

                view.Append("relayed " + detail, ConsoleColor.DarkGray);
            };

            bool stop = false;
            string stopReason = "Server stopped";
            session.Stopped += reason =>
            {
                stop = true;
                stopReason = reason;
            };

            try
            {
                session.Start(TcpHostTransport.Listen(options.Port), clock.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                view.Append("Could not listen on port " + options.Port + ": " + ex.Message);
                view.Draw(session, options, clock.ElapsedMilliseconds, "FAILED");
                Console.Error.WriteLine("Could not listen on port " + options.Port + ": " + ex.Message);
                Console.Error.WriteLine("(closing in 10 s; details also in " + FileLog.Path + ")");
                Thread.Sleep(10000);
                return 1;
            }

            if (options.OwnerKeyGenerated)
            {
                view.Append("Owner key " + options.OwnerKey + ": enter it in the game under Join > Owner key to be the host.", ConsoleColor.Yellow);
            }

            if (string.IsNullOrEmpty(options.GameVersion))
            {
                view.Append("No --game-version given: game build is not checked; players on different patches may desync.", ConsoleColor.Yellow);
            }

            if (options.ModCheck == "off")
            {
                view.Append("Mod check is off: players may join with different mods than the host.", ConsoleColor.Yellow);
            }

            view.Append(session.World != null
                ? "Holding world " + session.World.Info + "; joiners receive it automatically."
                : "No world yet: it is uploaded when the host is in a city.");

            view.Append("Ready on port " + options.Port + ". Commands: help, list, say <text>, speed <n|pause>, kick <id|name> [reason], stop", ConsoleColor.Cyan);

            ParentWatch parent = null;
            if (options.ParentPid > 0)
            {
                parent = ParentWatch.Open(options.ParentPid, out string failure);
                if (parent != null)
                {
                    view.Append("Bound to game process " + options.ParentPid + " via " + parent.Description + "; will exit when it does.");
                }
                else
                {
                    view.Append("Cannot watch game process " + options.ParentPid + " (" + failure + "); running unbound.");
                }
            }

            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                stop = true;
                stopReason = "Server stopped (Ctrl+C)";
            };

            watcher?.Start();
            updates?.Start();
            var plainReader = view.PanelMode ? null : new PlainCommandReader();
            long lastDraw = 0;
            long lastSettingsCheck = 0;
            DateTime settingsStamp = SettingsStamp(options.ConfigPath);
            long lastParentCheck = 0;
            long ownerGoneSince = -1;
            bool ownerEverJoined = false;

            while (!stop)
            {
                long now = clock.ElapsedMilliseconds;
                session.Update(now);

                PlaysetSnapshot playset;
                while (watcher != null && watcher.TryTake(out playset))
                {
                    ApplyPlayset(playset, session, store, view);
                }

                ReleaseInfo release;
                while (updates != null && updates.TryTake(out release))
                {
                    ReportRelease(release, options.UpdateRepository, session, view);
                }

                if (options.ConfigPath.Length > 0 && now - lastSettingsCheck > 10000)
                {
                    lastSettingsCheck = now;
                    DateTime stamp = SettingsStamp(options.ConfigPath);
                    if (stamp != settingsStamp)
                    {
                        settingsStamp = stamp;
                        ReloadSettings(args, options, session, store, view, ref watcher);
                    }
                }

                if (session.OwnerPlayerId != 0)
                {
                    ownerEverJoined = true;
                    ownerGoneSince = -1;
                }
                else if (options.ExitWhenOwnerLeaves && ownerEverJoined)
                {
                    if (ownerGoneSince < 0)
                    {
                        ownerGoneSince = now;
                        view.Append("Owner left; shutting down in " + options.OwnerGraceSeconds + " s unless they return.");
                    }
                    else if (now - ownerGoneSince > options.OwnerGraceSeconds * 1000L)
                    {
                        stop = true;
                        stopReason = "Host left the session";
                    }
                }

                if (parent != null && now - lastParentCheck > 1000)
                {
                    lastParentCheck = now;
                    if (parent.HasExited())
                    {
                        stop = true;
                        stopReason = "Host game closed";
                    }
                }

                string command = view.PanelMode ? view.PollCommand() : plainReader.TryRead();
                if (command != null && !RunCommand(command, session, options, view, ref stopReason))
                {
                    stop = true;
                }

                if (now - lastDraw > DrawIntervalMs)
                {
                    lastDraw = now;
                    string extra = ownerGoneSince >= 0 ? "owner gone, closing in " + Math.Max(0, options.OwnerGraceSeconds - (now - ownerGoneSince) / 1000) + " s" : null;
                    view.Draw(session, options, now, extra);
                }

                Thread.Sleep(TickMs);
            }

            session.Stop(stopReason);
            updates?.Dispose();
            watcher?.Dispose();
            parent?.Dispose();
            view.Draw(session, options, clock.ElapsedMilliseconds, "STOPPED");
            view.Append(stopReason);
            view.Shutdown();
            Thread.Sleep(300);
            return 0;
        }

        /// <summary>Returns false when the server should stop.</summary>
        private static DateTime SettingsStamp(string path)
        {
            try
            {
                return path.Length > 0 && System.IO.File.Exists(path) ? System.IO.File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            }
            catch (Exception)
            {
                return DateTime.MinValue;
            }
        }

        /// <summary>server.json changed on disk: apply what can change live, say what needs a restart.</summary>
        private static void ReloadSettings(string[] args, ServerOptions options, ServerSession session, WorldStore store, ConsoleView view, ref PlaysetWatcher watcher)
        {
            var warnings = new List<string>();
            ServerOptions fresh;
            try
            {
                fresh = ServerOptions.Parse(args, warnings);
            }
            catch (Exception ex)
            {
                view.Append("server.json not reloaded: " + ex.Message, ConsoleColor.Yellow);
                return;
            }

            foreach (string warning in warnings)
            {
                view.Append(warning, ConsoleColor.Yellow);
            }

            if (fresh.OwnerKeyGenerated)
            {
                fresh.OwnerKey = options.OwnerKey;
                fresh.OwnerKeyGenerated = options.OwnerKeyGenerated;
            }

            var changed = new List<string>();
            var restart = new List<string>();
            if (fresh.ServerName != options.ServerName)
            {
                options.ServerName = fresh.ServerName;
                session.Config.ServerName = fresh.ServerName;
                changed.Add("name");
            }

            if (fresh.Password != options.Password)
            {
                options.Password = fresh.Password;
                session.Config.Password = fresh.Password;
                changed.Add("password");
            }

            if (fresh.MaxPlayers != options.MaxPlayers)
            {
                options.MaxPlayers = fresh.MaxPlayers;
                session.Config.MaxPlayers = fresh.MaxPlayers;
                changed.Add("maxPlayers");
            }

            if (fresh.ModCheck != options.ModCheck)
            {
                options.ModCheck = fresh.ModCheck;
                session.Config.RequireMatchingMods = fresh.ModCheck != "off";
                session.Config.IgnoreModVersions = fresh.ModCheck != "strict";
                changed.Add("modCheck");
            }

            if (fresh.PlaysetHint != options.PlaysetHint)
            {
                options.PlaysetHint = fresh.PlaysetHint;
                session.Config.PlaysetHint = fresh.PlaysetHint;
                changed.Add("playsetHint");
            }

            if (fresh.Welcome != options.Welcome)
            {
                options.Welcome = fresh.Welcome;
                changed.Add("welcome");
            }

            if (!SameList(fresh.AcceptModVersions, options.AcceptModVersions))
            {
                options.AcceptModVersions = fresh.AcceptModVersions;
                session.Config.ExtraModVersions = new List<string>(fresh.AcceptModVersions);
                changed.Add("acceptModVersions");
            }

            bool modsChanged = !SameList(fresh.RequiredMods, options.RequiredMods);
            if (modsChanged)
            {
                options.RequiredMods = fresh.RequiredMods;
                changed.Add("requiredMods");
            }

            bool playsetChanged = fresh.PlaysetId != options.PlaysetId || fresh.PlaysetPollMinutes != options.PlaysetPollMinutes;
            if (playsetChanged)
            {
                options.PlaysetId = fresh.PlaysetId;
                options.PlaysetPollMinutes = fresh.PlaysetPollMinutes;
                changed.Add("playsetId");
                watcher?.Dispose();
                watcher = null;
                if (options.PlaysetId > 0)
                {
                    watcher = new PlaysetWatcher(options.PlaysetId, options.PlaysetPollMinutes, view);
                    watcher.Start();
                }
            }

            if (options.PlaysetId == 0 && (modsChanged || playsetChanged))
            {
                if (options.RequiredMods.Count > 0)
                {
                    session.SetReferenceMods(options.RequiredMods, "server.json", locked: true);
                    store?.SaveMods(options.RequiredMods, "server.json", view);
                }
                else
                {
                    // Back to learning the list from the host on their next join.
                    session.SetReferenceMods(session.ReferenceMods, session.ReferencePlayset, locked: false);
                }
            }

            if (fresh.Port != options.Port) restart.Add("port");
            if (fresh.OwnerKey != options.OwnerKey) restart.Add("ownerKey");
            if (fresh.GameVersion != options.GameVersion) restart.Add("gameVersion");
            if (fresh.DataDirectory != options.DataDirectory) restart.Add("dataDir");
            if (fresh.UpdateCheck != options.UpdateCheck || fresh.UpdateRepository != options.UpdateRepository) restart.Add("updateCheck");

            view.Append("server.json reloaded" + (changed.Count > 0 ? ": " + string.Join(", ", changed) : ": nothing changed")
                + (restart.Count > 0 ? ". Needs a restart for: " + string.Join(", ", restart) : ""), ConsoleColor.Cyan);
        }

        private static bool SameList(List<string> a, List<string> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }

            for (int i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>The newest GitHub release: say whether this server (and so the mod) is behind, and remember it for joiners.</summary>
        private static void ReportRelease(ReleaseInfo release, string repository, ServerSession session, ConsoleView view)
        {
            string running = "v" + Multiplayer.Core.Protocol.ProtocolConstants.ModVersion;
            if (GitHubReleases.IsNewer(release.Tag, Multiplayer.Core.Protocol.ProtocolConstants.ModVersion))
            {
                string page = release.Url.Length > 0 ? release.Url : GitHubReleases.ReleasesPage(repository);
                session.UpdateNotice = "Update available: " + release.Tag + " (this server runs " + running + "). Server download: " + page + " - players get the mod update from Paradox Mods.";
                view.Append(session.UpdateNotice, ConsoleColor.Yellow);
            }
            else
            {
                session.UpdateNotice = string.Empty;
                view.Append("Up to date: " + running + " is the latest release" + (release.Tag != running ? " (" + release.Tag + ")" : "") + ".", ConsoleColor.DarkGray);
            }
        }

        /// <summary>A freshly read published playset becomes the reference; players hear about additions and removals.</summary>
        private static void ApplyPlayset(PlaysetSnapshot playset, ServerSession session, WorldStore store, ConsoleView view)
        {
            IReadOnlyList<string> before = session.ReferenceMods;
            bool hadList = before != null;
            // Differences are only worth naming against a previous list; the first read is just the list.
            List<string> added = hadList ? ParadoxPlayset.AddedNames(before, playset.Mods) : new List<string>();
            List<string> removed = hadList ? ParadoxPlayset.AddedNames(playset.Mods, before) : new List<string>();
            string oldLabel = session.ReferencePlayset;

            session.SetReferenceMods(playset.Mods, playset.Label, locked: true);
            store?.SaveMods(playset.Mods, playset.Label, view);

            string change = string.Empty;
            if (added.Count > 0)
            {
                change += " added: " + string.Join(", ", added) + ";";
            }

            if (removed.Count > 0)
            {
                change += " removed: " + string.Join(", ", removed) + ";";
            }

            view.Append("Playset " + playset.Label + ": " + playset.Mods.Count + " mods" + (change.Length > 0 ? "," + change.TrimEnd(';') : hadList ? ", unchanged" : ""), ConsoleColor.Cyan);
            if (hadList && change.Length > 0 && !string.Equals(oldLabel, playset.Label, StringComparison.Ordinal))
            {
                session.Say("Playset '" + playset.Name + "' is now v" + playset.Version + ":" + change.TrimEnd(';') + ". Update the playset in Paradox Mods and restart the game to keep playing together.");
            }
        }

        private static bool RunCommand(string line, ServerSession session, ServerOptions options, ConsoleView view, ref string stopReason)
        {
            line = (line ?? string.Empty).Trim();
            if (line.Length == 0)
            {
                return true;
            }

            string[] parts = line.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            string verb = parts[0].ToLowerInvariant();
            string rest = parts.Length > 1 ? parts[1].Trim() : string.Empty;

            switch (verb)
            {
                case "help":
                case "?":
                    view.Append("help | list | world | mods | settings | version | say <text> | speed <0-3|pause> | kick <id|name> [reason] | stop");
                    return true;

                case "settings":
                case "config":
                    view.Append((options.ConfigPath.Length > 0 ? "Settings file: " + options.ConfigPath : "No settings file (server.json next to the program, or --config PATH)."));
                    view.Append("name '" + options.ServerName + "', port " + options.Port + ", password " + (options.Password.Length > 0 ? "set" : "none") + ", max players " + options.MaxPlayers
                        + ", game version " + (options.GameVersion.Length > 0 ? options.GameVersion : "any") + ", mod check " + options.ModCheck
                        + ", playset " + (options.PlaysetId > 0 ? options.PlaysetId.ToString() : "none") + ", required mods " + options.RequiredMods.Count
                        + ", welcome " + (options.Welcome.Length > 0 ? "set" : "none") + ", update check " + (options.UpdateCheck ? "on" : "off"));
                    return true;

                case "world":
                    view.Append(session.World != null
                        ? "World " + session.World.Info + " by " + session.World.Info.UploaderName + " at " + DateTimeOffset.FromUnixTimeSeconds(session.World.Info.SavedAtUnix).ToLocalTime().ToString("HH:mm:ss") + ", sha256 " + session.World.Info.Sha256.Substring(0, 12)
                        : "No world stored yet.");
                    return true;

                case "update":
                case "version":
                    view.Append("Running v" + Multiplayer.Core.Protocol.ProtocolConstants.ModVersion + ", protocol " + Multiplayer.Core.Protocol.ProtocolConstants.ProtocolVersion
                        + (session.UpdateNotice.Length > 0 ? ". " + session.UpdateNotice : ". No newer release known."));
                    return true;

                case "mods":
                    if (session.ReferenceMods == null)
                    {
                        view.Append("No reference mod list yet (learned when the host joins).");
                    }
                    else
                    {
                        view.Append("Required mods (" + session.ReferenceMods.Count + "): " + string.Join(", ", session.ReferenceMods));
                        if (session.ReferencePlayset.Length > 0)
                        {
                            view.Append((session.ReferenceLocked ? "Linked playset: " : "Host playset: ") + session.ReferencePlayset);
                        }
                    }

                    return true;

                case "list":
                case "players":
                    if (session.Players.Count == 0)
                    {
                        view.Append("No players connected.");
                    }
                    else
                    {
                        foreach (PlayerInfo player in session.Players)
                        {
                            view.Append("  " + player.PlayerId + ": " + player);
                        }
                    }

                    return true;

                case "say":
                    if (rest.Length == 0)
                    {
                        view.Append("Usage: say <text>");
                    }
                    else
                    {
                        session.Say(rest);
                        view.Append("<" + session.Config.ServerName + "> " + rest, ConsoleColor.White);
                    }

                    return true;

                case "speed":
                case "pause":
                {
                    float speed;
                    if (verb == "pause" || rest.Equals("pause", StringComparison.OrdinalIgnoreCase))
                    {
                        speed = 0f;
                    }
                    else if (!float.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out speed) || speed < 0f || speed > 10f)
                    {
                        view.Append("Usage: speed <0..10|pause>   (1 = normal, 0 = paused)");
                        return true;
                    }

                    session.SetSimulationSpeed(speed);
                    return true;
                }

                case "kick":
                {
                    string[] kickParts = rest.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (kickParts.Length == 0)
                    {
                        view.Append("Usage: kick <id|name> [reason]");
                        return true;
                    }

                    PlayerInfo target = session.FindPlayer(kickParts[0]);
                    if (target == null)
                    {
                        view.Append("No player '" + kickParts[0] + "'. Use list.");
                        return true;
                    }

                    session.KickPlayer(target.PlayerId, kickParts.Length > 1 ? kickParts[1] : "Kicked by the server");
                    return true;
                }

                case "stop":
                case "quit":
                case "exit":
                    stopReason = rest.Length > 0 ? rest : "Server stopped";
                    return false;

                default:
                    view.Append("Unknown command '" + verb + "'. Type help.");
                    return true;
            }
        }

        /// <summary>Plain mode: read whole lines on a background thread so the main loop never blocks.</summary>
        private sealed class PlainCommandReader
        {
            private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new System.Collections.Concurrent.ConcurrentQueue<string>();

            public PlainCommandReader()
            {
                var thread = new Thread(() =>
                {
                    try
                    {
                        string line;
                        while ((line = Console.ReadLine()) != null)
                        {
                            _lines.Enqueue(line);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }) { IsBackground = true, Name = "MP-Console" };
                thread.Start();
            }

            public string TryRead()
            {
                return _lines.TryDequeue(out string line) ? line : null;
            }
        }
    }
}
