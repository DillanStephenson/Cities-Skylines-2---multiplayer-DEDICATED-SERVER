using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Multiplayer.Core.Protocol;
using Multiplayer.Core.Session;
using Multiplayer.Core.Transport;
using Multiplayer.Core.Transport.Tcp;

namespace Multiplayer.SmokeClient
{
    /// <summary>
    /// Joins a server, prints everything that happens, says hello, optionally asks for a simulation speed,
    /// uploads a world file (owner) or downloads the server's world to a file, then leaves.
    /// Exit code 0 only if the handshake was accepted and any requested transfer succeeded.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string address = Arg(args, "-address", "127.0.0.1:" + ProtocolConstants.DefaultPort);
            string name = Arg(args, "-name", "SmokeClient");
            string password = Arg(args, "-password", "");
            string ownerKey = Arg(args, "-owner-key", "");
            string gameVersion = Arg(args, "-game-version", "");
            string modsText = Arg(args, "-mods", "");
            string playset = Arg(args, "-playset", "");
            int seconds = int.Parse(Arg(args, "-seconds", "15"));
            string speedText = Arg(args, "-speed", null);
            string uploadPath = Arg(args, "-upload", null);
            string downloadPath = Arg(args, "-download", null);
            string cityName = Arg(args, "-city", "Smoke City");
            string echoText = Arg(args, "-echo-build", null);
            float echoOffset = echoText != null ? float.Parse(echoText, System.Globalization.CultureInfo.InvariantCulture) : float.NaN;
            var pendingEchoes = new Queue<(long dueMs, byte[] payload, string kind)>();

            if (!EndpointParser.TryParse(address, ProtocolConstants.DefaultPort, out string host, out int port))
            {
                Console.Error.WriteLine("Bad address: " + address);
                return 2;
            }

            var mods = new List<string>();
            foreach (string mod in modsText.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                mods.Add(mod.Trim());
            }

            var config = new ClientConfig { PlayerName = name, Password = password, OwnerKey = ownerKey, GameVersion = gameVersion, Mods = mods, Playset = playset };
            var session = new ClientSession(config, new ConsoleLog());
            bool accepted = false;
            bool transferOk = uploadPath == null && downloadPath == null;
            bool transferDone = transferOk;

            session.StateChanged += state =>
            {
                Console.WriteLine("state -> " + state + (state == SessionState.Failed ? " (" + session.LastError + ")" : ""));
                if (state == SessionState.Connected)
                {
                    accepted = true;
                }
            };
            session.PlayerJoined += player => Console.WriteLine("joined: " + player);
            session.PlayerLeft += (player, reason) => Console.WriteLine("left: " + player + " (" + reason + ")");
            session.ChatReceived += (player, text) => Console.WriteLine("chat  " + player.Name + ": " + text);
            session.SimulationSpeedReceived += speed => Console.WriteLine("speed -> " + speed);
            var clockForEcho = Stopwatch.StartNew();
            bool echoState = Arg(args, "-echo-state", null) != null;
            bool echoPolicy = Arg(args, "-echo-policy", null) != null;
            session.GameplayCommandReceived += command =>
            {
                Console.WriteLine("command " + command.Kind + " from " + command.OriginPlayerId + " (" + command.Payload.Length + " bytes)");
                if (command.Kind == Multiplayer.Core.Build.CityStateCommand.Kind)
                {
                    try
                    {
                        var state = Multiplayer.Core.Build.CityStateCommand.FromBytes(command.Payload);
                        Console.WriteLine("  " + state + (state.Entries.Count <= 8 ? ": " + string.Join(", ", state.Entries) : ""));
                        if (echoState && !state.FullSnapshot)
                        {
                            var reply = new Multiplayer.Core.Build.CityStateCommand();
                            foreach (var entry in state.Entries)
                            {
                                if (entry.Key.StartsWith("tax:area:", StringComparison.Ordinal))
                                {
                                    reply.Entries.Add(new Multiplayer.Core.Build.StateEntry(entry.Key, entry.Value + 1f));
                                }
                            }

                            if (reply.Entries.Count > 0)
                            {
                                pendingEchoes.Enqueue((clockForEcho.ElapsedMilliseconds + 1500, reply.ToBytes(), Multiplayer.Core.Build.CityStateCommand.Kind));
                                Console.WriteLine("  will echo the tax change back one point higher in 1.5 s");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("  could not decode city state: " + ex.Message);
                    }

                    return;
                }

                if (command.Kind == Multiplayer.Core.Build.PolicyCommand.Kind)
                {
                    try
                    {
                        var policy = Multiplayer.Core.Build.PolicyCommand.FromBytes(command.Payload);
                        Console.WriteLine("  " + policy);
                        if (echoPolicy)
                        {
                            policy.Active = !policy.Active;
                            pendingEchoes.Enqueue((clockForEcho.ElapsedMilliseconds + 1500, policy.ToBytes(), Multiplayer.Core.Build.PolicyCommand.Kind));
                            Console.WriteLine("  will echo it back inverted (" + (policy.Active ? "on" : "off") + ") in 1.5 s");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("  could not decode policy command: " + ex.Message);
                    }

                    return;
                }

                if (command.Kind != Multiplayer.Core.Build.BuildCommand.Kind)
                {
                    return;
                }

                try
                {
                    var build = Multiplayer.Core.Build.BuildCommand.FromBytes(command.Payload);
                    Console.WriteLine("  " + build);
                    foreach (var definition in build.Definitions)
                    {
                        Console.WriteLine("    " + definition + (definition.Course != null ? " from " + definition.Course.Start.Position + " to " + definition.Course.End.Position : definition.Object != null ? " at " + definition.Object.Position : definition.Brush != null ? " stroke at " + definition.Brush.LineA : ""));
                    }

                    if (!float.IsNaN(echoOffset))
                    {
                        Multiplayer.Core.Build.BuildCommand shifted = Shift(build, echoOffset);
                        pendingEchoes.Enqueue((clockForEcho.ElapsedMilliseconds + 1500, shifted.ToBytes(), Multiplayer.Core.Build.BuildCommand.Kind));
                        Console.WriteLine("  will echo it back shifted by " + echoOffset + " m on X in 1.5 s");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  could not decode build command: " + ex.Message);
                }
            };
            session.WorldInfoReceived += info => Console.WriteLine(info != null ? "world: " + info + " by " + info.UploaderName : "world: none on server");
            session.WorldDownloadProgress += (got, total) =>
            {
                if (got == total || got % (4 * ProtocolConstants.WorldChunkBytes) == 0)
                {
                    Console.WriteLine("download " + (100 * got / Math.Max(1, total)) + "%");
                }
            };
            session.WorldDownloaded += snapshot =>
            {
                File.WriteAllBytes(downloadPath, snapshot.Data);
                Console.WriteLine("downloaded " + snapshot.Info + " to " + downloadPath + " (sha256 " + snapshot.Info.Sha256.Substring(0, 12) + ")");
                transferOk = true;
                transferDone = true;
            };
            session.WorldDownloadFailed += reason =>
            {
                Console.WriteLine("download failed: " + reason);
                transferDone = true;
            };
            session.WorldUploadFinished += (ok, revision, reason) =>
            {
                Console.WriteLine(ok ? "upload accepted as revision " + revision : "upload rejected: " + reason);
                transferOk = ok;
                transferDone = true;
            };

            var clock = Stopwatch.StartNew();
            Console.WriteLine("connecting to " + host + ":" + port + " as '" + name + "'" + (ownerKey.Length > 0 ? " with owner key" : "") + (mods.Count > 0 ? " with mods " + string.Join(",", mods) : ""));
            session.Join(TcpClientTransport.Connect(host, port, config.ConnectTimeoutMs), clock.ElapsedMilliseconds);

            bool said = false;
            bool askedSpeed = false;
            bool transferStarted = false;
            long deadline = seconds * 1000L;
            while (clock.ElapsedMilliseconds < deadline)
            {
                session.Update(clock.ElapsedMilliseconds);

                if (session.State == SessionState.Connected && !said)
                {
                    said = true;
                    Console.WriteLine("server: " + session.ServerName + ", me: player " + session.LocalPlayerId + (session.IsOwner ? " (owner)" : ""));
                    Console.WriteLine("players: " + string.Join(", ", session.Players));
                    session.SendChat("hello from the smoke client");
                }

                if (session.State == SessionState.Connected && said && !askedSpeed && speedText != null && clock.ElapsedMilliseconds > 2000)
                {
                    askedSpeed = true;
                    float speed = float.Parse(speedText, System.Globalization.CultureInfo.InvariantCulture);
                    Console.WriteLine("asking server for speed " + speed);
                    session.SubmitSimulationSpeed(speed);
                }

                if (session.State == SessionState.Connected && session.ServerWorldKnown && !transferStarted)
                {
                    transferStarted = true;
                    if (uploadPath != null)
                    {
                        byte[] data = File.ReadAllBytes(uploadPath);
                        Console.WriteLine("uploading " + uploadPath + " (" + WorldInfo.FormatSize(data.Length) + ")");
                        if (!session.UploadWorld(Path.GetFileNameWithoutExtension(uploadPath), cityName, Guid.NewGuid().ToString("N"), data))
                        {
                            Console.WriteLine("upload not started (not the owner?)");
                            transferDone = true;
                        }
                    }
                    else if (downloadPath != null)
                    {
                        if (session.ServerWorld == null)
                        {
                            Console.WriteLine("nothing to download: the server holds no world");
                            transferDone = true;
                        }
                        else if (!session.RequestWorld())
                        {
                            Console.WriteLine("download not started");
                            transferDone = true;
                        }
                    }
                }

                if (transferStarted && !transferDone)
                {
                    // Keep going while a transfer is in flight, up to ten minutes.
                    deadline = Math.Max(deadline, Math.Min(clock.ElapsedMilliseconds + 2000, 600000));
                }

                while (pendingEchoes.Count > 0 && pendingEchoes.Peek().dueMs <= clockForEcho.ElapsedMilliseconds)
                {
                    var echo = pendingEchoes.Dequeue();
                    session.SendGameplayCommand(echo.kind, echo.payload);
                    Console.WriteLine("echoed a " + echo.kind + " command (" + echo.payload.Length + " bytes)");
                }

                if (session.State == SessionState.Failed)
                {
                    break;
                }

                Thread.Sleep(10);
            }

            if (session.IsOnline)
            {
                session.Leave("smoke client done");
            }

            bool success = accepted && transferOk;
            Console.WriteLine(success ? "RESULT: accepted" : "RESULT: not accepted (" + (accepted ? "transfer failed" : session.LastError) + ")");
            return success ? 0 : 1;
        }

        /// <summary>Copy of a build command with every position moved along X, so an echo lands beside the original.</summary>
        private static Multiplayer.Core.Build.BuildCommand Shift(Multiplayer.Core.Build.BuildCommand source, float dx)
        {
            var copy = Multiplayer.Core.Build.BuildCommand.FromBytes(source.ToBytes());
            copy.Sequence += 1000;
            copy.ToolId = source.ToolId + " (echo)";
            foreach (var definition in copy.Definitions)
            {
                ShiftRef(definition.Original, dx);
                ShiftRef(definition.Owner, dx);
                ShiftRef(definition.Attached, dx);
                if (definition.Course != null)
                {
                    definition.Course.A.X += dx;
                    definition.Course.B.X += dx;
                    definition.Course.C.X += dx;
                    definition.Course.D.X += dx;
                    definition.Course.Start.Position.X += dx;
                    definition.Course.End.Position.X += dx;
                    ShiftRef(definition.Course.Start.Entity, dx);
                    ShiftRef(definition.Course.End.Entity, dx);
                }

                if (definition.Object != null)
                {
                    definition.Object.Position.X += dx;
                }

                if (definition.OwnerDefinition != null)
                {
                    definition.OwnerDefinition.Position.X += dx;
                }

                if (definition.AreaNodes != null)
                {
                    for (int i = 0; i < definition.AreaNodes.Count; i++)
                    {
                        var node = definition.AreaNodes[i];
                        node.Position.X += dx;
                        definition.AreaNodes[i] = node;
                    }
                }

                if (definition.Zoning != null)
                {
                    definition.Zoning.A.X += dx;
                    definition.Zoning.B.X += dx;
                    definition.Zoning.C.X += dx;
                    definition.Zoning.D.X += dx;
                }

                if (definition.Brush != null)
                {
                    definition.Brush.LineA.X += dx;
                    definition.Brush.LineB.X += dx;
                    definition.Brush.Target.X += dx;
                    definition.Brush.Start.X += dx;
                }

                if (definition.Waypoints != null)
                {
                    foreach (var waypoint in definition.Waypoints)
                    {
                        waypoint.Position.X += dx;
                        ShiftRef(waypoint.Connection, dx);
                        ShiftRef(waypoint.Original, dx);
                    }
                }
            }

            return copy;
        }

        private static void ShiftRef(Multiplayer.Core.Build.EntityRef reference, float dx)
        {
            // Lines are identified by number, not position; everything else moves with the echo.
            if (reference != null && reference.Kind != Multiplayer.Core.Build.EntityKind.None && reference.Kind != Multiplayer.Core.Build.EntityKind.Route)
            {
                reference.Position.X += dx;
                reference.Aux.X += dx;
            }
        }

        private static string Arg(string[] args, string key, string fallback)
        {
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return fallback;
        }

        private sealed class ConsoleLog : ISessionLog
        {
            public void Info(string message) => Console.WriteLine("  log: " + message);

            public void Warn(string message) => Console.WriteLine("  warn: " + message);

            public void Error(string message) => Console.WriteLine("  error: " + message);
        }
    }
}
