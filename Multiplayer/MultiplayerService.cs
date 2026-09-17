using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Colossal.Logging;
using Game;
using Game.SceneFlow;
using Multiplayer.Core.Build;
using Multiplayer.Core.Protocol;
using Multiplayer.Core.Session;
using Multiplayer.Core.Transport;
using Multiplayer.Core.Transport.Tcp;
using Unity.Entities;

namespace Multiplayer
{
    /// <summary>
    /// Game-side owner of the client session. Host = spawn the server window next to the game and join it
    /// with the owner key; Join = connect to someone else's server window. Keeps a human-readable status
    /// for the options page and hands server decisions to the ECS system. Everything here runs on the game thread.
    /// </summary>
    public sealed class MultiplayerService
    {
        public const string ServerExecutable = "Multiplayer.Server.exe";

        private const int RecentLineLimit = 8;
        private const int ServerStartRetryMs = 1000;
        private const int ServerStartGiveUpMs = 15000;

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly ILog _log;
        private readonly Setting _settings;
        private readonly string _modDirectory;
        private readonly List<string> _recent = new List<string>();

        private float? _pendingSpeed;
        private Process _serverProcess;
        private string _spawnedOwnerKey;
        private int _spawnedPort;
        private long _serverSpawnedAtMs = -1;
        private long _lastConnectAttemptMs;

        public ClientSession Session { get; }

        public WorldSyncService WorldSync { get; }

        public string StatusText { get; private set; } = "Offline";

        /// <summary>The last few events, newest last, as shown in the status box and the menu screen.</summary>
        public IReadOnlyList<string> RecentLines => _recent;

        public bool IsOnline => Session.IsOnline || IsWaitingForSpawnedServer;

        /// <summary>True between spawning the server window and the first successful handshake with it.</summary>
        public bool IsWaitingForSpawnedServer => _serverSpawnedAtMs >= 0 && !Session.IsOnline;

        public bool IsHostingLocally => _serverProcess != null && !ProcessHasExited(_serverProcess);

        private long Now => _clock.ElapsedMilliseconds;

        /// <summary>The service clock, for systems that time-stamp what arrives.</summary>
        public long NowMs => Now;

        /// <summary>The player whose game does the automatic saves: the owner, or the longest-connected player without one.</summary>
        public bool IsLeader => Session.IsLeader;

        public MultiplayerService(Setting settings, ILog log, string modDirectory)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _modDirectory = modDirectory ?? string.Empty;

            Session = new ClientSession(new ClientConfig { GameVersion = ReadGameVersion() }, new LogAdapter(log));
            Session.StateChanged += OnStateChanged;
            Session.PlayerJoined += player => Note(player + " joined");
            Session.PlayerLeft += (player, reason) => Note(player + " left (" + reason + ")");
            Session.PlayerLeft += (player, reason) => Sync.PresenceStore.Remove(player.PlayerId);
            Session.ChatReceived += (player, text) => Note(player.Name + ": " + text);
            Session.SimulationSpeedReceived += speed => _pendingSpeed = speed;
            Session.GameplayCommandReceived += OnGameplayCommand;

            WorldSync = new WorldSyncService(Session, settings, log, Note);

            RefreshStatus();
        }

        // ---------------------------------------------------------------- build sync

        private int _buildsSent;
        private int _buildsReceived;
        private bool _devBuildRequested;
        private int _devStep;
        private long _devNextAtMs = -1;
        private Unity.Mathematics.float3 _devRoadStart;
        private int _devTaxBefore;
        private Unity.Mathematics.float3 _devTerrainSpot;
        private float _devHeightBefore;
        private float _devHeightBeforeEcho;
        private Entity _devPolicyBuilding;
        private Entity _devPolicy;
        private const float DevEchoOffset = 150f;

        private Sync.BuildReplaySystem Replay
        {
            get
            {
                World world = World.DefaultGameObjectInjectionWorld;
                return world != null ? world.GetExistingSystemManaged<Sync.BuildReplaySystem>() : null;
            }
        }

        /// <summary>Called by the capture system in the apply frame with what the local player just built.</summary>
        public void SendBuild(BuildCommand command)
        {
            byte[] bytes = command.ToBytes();
            Session.SendGameplayCommand(BuildCommand.Kind, bytes);
            _buildsSent++;
            _log.Info("Sent " + command + ", " + bytes.Length + " bytes: " + Summarize(command));
            RefreshStatus();
        }

        private int _stateSent;
        private int _stateReceived;

        /// <summary>Called by the city state system with what changed locally (taxes, budgets, fees, policies).</summary>
        public void SendCityState(CityStateCommand command)
        {
            Session.SendGameplayCommand(CityStateCommand.Kind, command.ToBytes());
            _stateSent++;
            _log.Info("Sent " + command + (command.Entries.Count <= 6 ? ": " + string.Join(", ", command.Entries.ConvertAll(e => e.ToString()).ToArray()) : ""));
        }

        private int _policiesSent;
        private int _policiesReceived;

        /// <summary>Called by the presence system a few times a second with where this player is looking.</summary>
        public void SendPresence(PresenceCommand command)
        {
            Session.SendGameplayCommand(PresenceCommand.Kind, command.ToBytes());
        }

        /// <summary>Called by the policy sync system with a policy the local player set on a building, district or line.</summary>
        public void SendPolicy(PolicyCommand command)
        {
            Session.SendGameplayCommand(PolicyCommand.Kind, command.ToBytes());
            _policiesSent++;
            _log.Info("Sent " + command);
        }

        private void OnGameplayCommand(GameplayCommandMessage message)
        {
            if (message.Kind == PresenceCommand.Kind)
            {
                try
                {
                    PresenceCommand presence = PresenceCommand.FromBytes(message.Payload);
                    string name = null;
                    foreach (PlayerInfo player in Session.Players)
                    {
                        if (player.PlayerId == message.OriginPlayerId)
                        {
                            name = player.Name;
                            break;
                        }
                    }

                    Sync.PresenceStore.Receive(message.OriginPlayerId, name, presence, Now);
                }
                catch (Exception ex)
                {
                    _log.Warn("Bad presence from player " + message.OriginPlayerId + ": " + ex.Message);
                }

                return;
            }

            if (message.Kind == PolicyCommand.Kind)
            {
                try
                {
                    PolicyCommand policy = PolicyCommand.FromBytes(message.Payload);
                    _policiesReceived++;
                    World world = World.DefaultGameObjectInjectionWorld;
                    Sync.PolicySyncSystem system = world != null ? world.GetExistingSystemManaged<Sync.PolicySyncSystem>() : null;
                    if (system != null)
                    {
                        system.Receive(policy);
                    }

                    _log.Info("Received " + policy + " from player " + message.OriginPlayerId);
                }
                catch (Exception ex)
                {
                    _log.Warn("Bad policy command from player " + message.OriginPlayerId + ": " + ex.Message);
                }

                return;
            }

            if (message.Kind == CityStateCommand.Kind)
            {
                try
                {
                    CityStateCommand state = CityStateCommand.FromBytes(message.Payload);
                    _stateReceived++;
                    World world = World.DefaultGameObjectInjectionWorld;
                    Sync.CityStateSyncSystem system = world != null ? world.GetExistingSystemManaged<Sync.CityStateSyncSystem>() : null;
                    if (system != null)
                    {
                        system.Receive(state);
                    }

                    _log.Info("Received " + state + " from player " + message.OriginPlayerId + (state.Entries.Count <= 6 ? ": " + string.Join(", ", state.Entries.ConvertAll(e => e.ToString()).ToArray()) : ""));
                }
                catch (Exception ex)
                {
                    _log.Warn("Bad city state from player " + message.OriginPlayerId + ": " + ex.Message);
                }

                return;
            }

            if (message.Kind != BuildCommand.Kind)
            {
                _log.Info("Gameplay command '" + message.Kind + "' from player " + message.OriginPlayerId + " (" + message.Payload.Length + " bytes) - no handler");
                return;
            }

            BuildCommand command;
            try
            {
                command = BuildCommand.FromBytes(message.Payload);
            }
            catch (Exception ex)
            {
                _log.Warn("Bad build command from player " + message.OriginPlayerId + ": " + ex.Message);
                return;
            }

            _buildsReceived++;
            _log.Info("Received " + command + " from player " + message.OriginPlayerId + ": " + Summarize(command));
            Sync.BuildReplaySystem replay = Replay;
            if (replay == null)
            {
                _log.Warn("Replay system missing; dropping " + command);
                return;
            }

            replay.Enqueue(command, message.OriginPlayerId);
            RefreshStatus();
        }

        /// <summary>Dev trigger: build a short road here through the replay engine, but let capture broadcast it.</summary>
        public void RequestDevBuild()
        {
            _devBuildRequested = true;
        }

        private void RunDevBuildIfDue()
        {
            if (!_devBuildRequested || Session.State != SessionState.Connected)
            {
                return;
            }

            GameManager manager = GameManager.instance;
            if (manager == null || manager.gameMode != GameMode.Game || manager.isGameLoading)
            {
                return;
            }

            if (_devNextAtMs >= 0 && Now < _devNextAtMs)
            {
                return;
            }

            World world = World.DefaultGameObjectInjectionWorld;
            var echo = new Unity.Mathematics.float3(DevEchoOffset, 0f, 0f);
            var mid = _devRoadStart + new Unity.Mathematics.float3(Sync.DevBuildTest.RoadLength * 0.5f, 0f, 0f);
            var treeSpot = mid + new Unity.Mathematics.float3(0f, 0f, 30f);
            BuildCommand command = null;
            switch (_devStep)
            {
                case 0:
                    command = Sync.DevBuildTest.MakeStraightRoad(world, _log, out _devRoadStart);
                    break;
                case 1:
                    Note("Dev check road: " + Sync.DevBuildTest.CountNodesNear(world, _devRoadStart, 20f) + " node(s) at start, "
                        + Sync.DevBuildTest.CountNodesNear(world, _devRoadStart + echo, 20f) + " at the echo");
                    command = Sync.DevBuildTest.MakeTree(world, _log, treeSpot);
                    break;
                case 2:
                    Note("Dev check tree: " + Sync.DevBuildTest.CountObjectsNear(world, treeSpot, 5f) + " object(s) at the tree spot, "
                        + Sync.DevBuildTest.CountObjectsNear(world, treeSpot + echo, 5f) + " at the echo");
                    command = Sync.DevBuildTest.MakeZoneFill(world, _log, mid);
                    break;
                case 3:
                    Note("Dev check zoning: " + Sync.DevBuildTest.CountZonedCellsNear(world, mid, 60f) + " zoned cell(s) by the road, "
                        + Sync.DevBuildTest.CountZonedCellsNear(world, mid + echo, 60f) + " at the echo");
                    command = Sync.DevBuildTest.MakeBulldozeEdge(world, _log, mid);
                    break;
                case 4:
                {
                    Note("Dev check bulldoze: " + Sync.DevBuildTest.CountNodesNear(world, _devRoadStart, 20f) + " node(s) left at start, "
                        + Sync.DevBuildTest.CountNodesNear(world, _devRoadStart + echo, 20f) + " at the echo (0 and 0 means both deleted)");
                    // City settings: raise residential tax by one point locally; the state sync should broadcast it,
                    // and the console client echoes it back one point higher still.
                    var taxSystem = world.GetExistingSystemManaged<Game.Simulation.TaxSystem>();
                    _devTaxBefore = taxSystem.GetTaxRate(Game.Simulation.TaxAreaType.Residential);
                    taxSystem.SetTaxRate(Game.Simulation.TaxAreaType.Residential, _devTaxBefore + 1);
                    Note("Dev tax: residential " + _devTaxBefore + " -> " + (_devTaxBefore + 1) + " set locally");
                    break;
                }

                case 5:
                {
                    var taxSystem = world.GetExistingSystemManaged<Game.Simulation.TaxSystem>();
                    int now = taxSystem.GetTaxRate(Game.Simulation.TaxAreaType.Residential);
                    Note("Dev check tax: residential is now " + now + " (started at " + _devTaxBefore + "; +2 means the echo from the other player was applied)");
                    taxSystem.SetTaxRate(Game.Simulation.TaxAreaType.Residential, _devTaxBefore);

                    // Terrain: a few raise strokes beside where the road was; the echo lands 150 m along.
                    _devTerrainSpot = mid + new Unity.Mathematics.float3(0f, 0f, -40f);
                    _devHeightBefore = Sync.DevBuildTest.HeightAt(world, _devTerrainSpot);
                    _devHeightBeforeEcho = Sync.DevBuildTest.HeightAt(world, _devTerrainSpot + echo);
                    command = Sync.DevBuildTest.MakeTerrainRaise(world, _log, _devTerrainSpot);
                    break;
                }

                case 6:
                {
                    float here = Sync.DevBuildTest.HeightAt(world, _devTerrainSpot) - _devHeightBefore;
                    float there = Sync.DevBuildTest.HeightAt(world, _devTerrainSpot + echo) - _devHeightBeforeEcho;
                    Note("Dev check terrain: " + here.ToString("0.00") + " m change at the spot, " + there.ToString("0.00") + " m at the echo (both above zero means the strokes were relayed)");

                    // Building option: switch a service building off through the game's policy path; the console
                    // client echoes the change back inverted, which should switch it on again.
                    if (Sync.DevBuildTest.FindBuildingPolicy(world, _log, out _devPolicyBuilding, out _devPolicy))
                    {
                        var policies = world.GetExistingSystemManaged<Game.UI.InGame.PoliciesUISystem>();
                        policies.SetPolicy(_devPolicyBuilding, _devPolicy, true);
                        Note("Dev policy: set active locally on " + Sync.DevBuildTest.DescribeEntity(world, _devPolicyBuilding) + "; the echo comes back inactive");
                    }
                    else
                    {
                        Note("Dev policy: no service building with a switch-off option here; skipping that check");
                    }

                    break;
                }

                case 7:
                {
                    if (_devPolicyBuilding != Entity.Null)
                    {
                        bool active = Sync.DevBuildTest.IsPolicyActive(world, _devPolicyBuilding, _devPolicy);
                        Note("Dev check policy: " + (active ? "still active" : "inactive") + " (set active locally; inactive means the inverted echo from the other player was applied)");
                        if (active)
                        {
                            world.GetExistingSystemManaged<Game.UI.InGame.PoliciesUISystem>().SetPolicy(_devPolicyBuilding, _devPolicy, false);
                        }
                    }

                    Note("Dev build sequence finished");
                    _devBuildRequested = false;
                    _devNextAtMs = -1;
                    return;
                }
            }

            if (command == null && _devStep == 0)
            {
                Note("Dev build: could not make a test road (see log)");
                _devBuildRequested = false;
                return;
            }

            if (command != null)
            {
                command.Sequence = _devStep;
                Replay?.Enqueue(command, Session.LocalPlayerId, captureAnyway: true);
                Note("Dev build step " + _devStep + " queued: " + command.ToolId);
            }

            _devStep++;
            _devNextAtMs = Now + 6000;
        }

        /// <summary>Settings button: the host pushes the city they are playing to the server.</summary>
        public void UploadCityNow()
        {
            WorldSync.UploadNow(Now);
            RefreshStatus();
        }

        /// <summary>Settings button: fetch and load the server's city.</summary>
        public void FetchCityNow()
        {
            WorldSync.DownloadNow(Now);
            RefreshStatus();
        }

        // ---------------------------------------------------------------- settings-page actions

        /// <summary>Spawn the server window and join it as owner.</summary>
        public void HostGame()
        {
            if (IsOnline)
            {
                return;
            }

            if (!EndpointParser.TryParsePort(_settings.HostPort, out int port))
            {
                Note("Invalid port '" + _settings.HostPort + "'");
                return;
            }

            string exe = Path.Combine(_modDirectory, ServerExecutable);
            if (!File.Exists(exe))
            {
                Note("Server window not found: " + exe);
                _log.Warn("Expected the server executable next to the mod DLL at " + exe);
                return;
            }

            string ownerKey = Guid.NewGuid().ToString("N");
            var argumentBuilder = new StringBuilder();
            argumentBuilder.Append("--port ").Append(port);
            if (!string.IsNullOrEmpty(_settings.HostPassword))
            {
                // Base64 so any character survives the command line; omitted entirely when empty.
                argumentBuilder.Append(" --password-base64 ").Append(Quote(Base64(_settings.HostPassword)));
            }

            argumentBuilder.Append(" --owner-key-base64 ").Append(Quote(Base64(ownerKey)));
            argumentBuilder.Append(" --game-version ").Append(Quote(ReadGameVersion()));
            argumentBuilder.Append(" --name ").Append(Quote(SafeServerName(_settings.PlayerName)));
            argumentBuilder.Append(" --max-players 8");
            argumentBuilder.Append(" --parent-pid ").Append(Process.GetCurrentProcess().Id);
            argumentBuilder.Append(" --exit-when-owner-leaves");
            argumentBuilder.Append(" --data-dir ").Append(Quote(Path.Combine(UnityEngine.Application.persistentDataPath, "ModsData", Mod.Name, "server")));
            string arguments = argumentBuilder.ToString();
            _log.Info("Starting server window: " + ServerExecutable + " " + arguments.Replace(Base64(ownerKey), "<owner-key>"));

            try
            {
                var startInfo = new ProcessStartInfo(exe, arguments)
                {
                    UseShellExecute = true,
                    WorkingDirectory = _modDirectory,
                    WindowStyle = ProcessWindowStyle.Normal,
                };
                _serverProcess = Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                _log.Warn("Could not start the server window: " + ex);
                Note("Could not start the server window: " + ex.Message);
                return;
            }

            _spawnedOwnerKey = ownerKey;
            _spawnedPort = port;
            _serverSpawnedAtMs = Now;
            _lastConnectAttemptMs = -ServerStartRetryMs;
            Note("Server window started on port " + port + " (pid " + (_serverProcess != null ? _serverProcess.Id.ToString() : "?") + "), joining it...");
        }

        /// <summary>Connect to a server window someone else is running.</summary>
        public void JoinGame()
        {
            if (IsOnline)
            {
                return;
            }

            if (!EndpointParser.TryParse(_settings.JoinAddress, ProtocolConstants.DefaultPort, out string host, out int port))
            {
                Note("Invalid address '" + _settings.JoinAddress + "'");
                return;
            }

            Connect(host, port, _settings.JoinPassword, _settings.JoinOwnerKey);
        }

        public void Leave()
        {
            EndSession("Host closed the session", "Left the session");
            RefreshStatus();
        }

        public void Shutdown()
        {
            EndSession("Host game closed", "Mod unloaded");
        }

        /// <summary>
        /// Leave, and if we own a server window, ask it to stop first so everyone hears why. Never blocks:
        /// the window exits on its own (owner gone, or the game's process id disappearing).
        /// </summary>
        private void EndSession(string ownerReason, string clientReason)
        {
            bool waitingForWindow = _serverSpawnedAtMs >= 0 && !Session.IsOnline;
            _serverSpawnedAtMs = -1;
            _spawnedOwnerKey = null;

            if (Session.IsOnline)
            {
                // Only the window this game spawned is ours to stop; a dedicated server keeps running for the others.
                if (Session.IsOwner && IsHostingLocally)
                {
                    Session.RequestServerStop(ownerReason);
                }

                Session.Leave(Session.IsOwner ? ownerReason : clientReason);
            }
            else if (waitingForWindow)
            {
                Note("Stopped waiting for the server window");
                KillSpawnedServer();
            }

            _serverProcess = null;
        }

        // ---------------------------------------------------------------- per-frame bridge

        public void Update()
        {
            try
            {
                Session.Update(Now);
                PollSpawnedServer();
                WorldSync.Update(Now);
                RunDevBuildIfDue();
            }
            catch (Exception ex)
            {
                _log.Error("Session update failed: " + ex);
                if (Session.IsOnline)
                {
                    Session.Leave("Internal error");
                }
            }
        }

        /// <summary>Speed the server announced, waiting to be pushed into the simulation. Consumed once.</summary>
        public bool TryTakePendingSpeed(out float speed)
        {
            if (_pendingSpeed.HasValue)
            {
                speed = _pendingSpeed.Value;
                _pendingSpeed = null;
                return true;
            }

            speed = 0f;
            return false;
        }

        public void SubmitLocalSpeed(float speed)
        {
            Session.SubmitSimulationSpeed(speed);
        }

        // ---------------------------------------------------------------- helpers

        private void Connect(string host, int port, string password, string ownerKey)
        {
            Session.Config.PlayerName = _settings.PlayerName;
            Session.Config.Password = password ?? string.Empty;
            Session.Config.OwnerKey = ownerKey ?? string.Empty;
            Session.Config.ModVersion = ProtocolConstants.ModVersion;
            Session.Config.Mods = ModList.Current();
            Session.Config.Playset = ModList.ActivePlaysetName();

            try
            {
                TcpClientTransport transport = TcpClientTransport.Connect(host, port, Session.Config.ConnectTimeoutMs);
                Session.Join(transport, Now);
                Note("Connecting to " + host + ":" + port + "...");
            }
            catch (Exception ex)
            {
                _log.Warn("Could not connect: " + ex);
                Note("Could not connect: " + ex.Message);
            }
        }

        /// <summary>While the spawned server window is starting up, keep trying to join it; give up after a while.</summary>
        private void PollSpawnedServer()
        {
            if (_serverSpawnedAtMs < 0 || Session.IsOnline)
            {
                return;
            }

            if (Session.State == SessionState.Connecting || Session.State == SessionState.Handshaking)
            {
                return;
            }

            if (_serverProcess != null && ProcessHasExited(_serverProcess))
            {
                Note("The server window closed (exit code " + SafeExitCode(_serverProcess) + "); check its output. Is port " + _spawnedPort + " free?");
                _serverSpawnedAtMs = -1;
                _serverProcess = null;
                return;
            }

            if (Now - _serverSpawnedAtMs > ServerStartGiveUpMs)
            {
                Note("Gave up waiting for the server window after " + (ServerStartGiveUpMs / 1000) + " s");
                _serverSpawnedAtMs = -1;
                KillSpawnedServer();
                return;
            }

            if (Now - _lastConnectAttemptMs < ServerStartRetryMs)
            {
                return;
            }

            _lastConnectAttemptMs = Now;
            Connect("127.0.0.1", _spawnedPort, _settings.HostPassword, _spawnedOwnerKey);
        }

        private void OnStateChanged(SessionState state)
        {
            if (state == SessionState.Connected)
            {
                bool hosting = _serverSpawnedAtMs >= 0;
                _serverSpawnedAtMs = -1;
                Note(hosting
                    ? "Hosting: server window is up, you are the owner (player " + Session.LocalPlayerId + ")"
                    : "Joined '" + Session.ServerName + "' as player " + Session.LocalPlayerId + (Session.IsOwner ? " (owner)" : ""));
                return;
            }

            if (state == SessionState.Failed)
            {
                // While the server window is still starting, connection failures are expected; PollSpawnedServer retries quietly.
                if (_serverSpawnedAtMs >= 0 && Session.LastError.StartsWith("Could not connect", StringComparison.Ordinal))
                {
                    RefreshStatus();
                    return;
                }

                Note("Disconnected: " + Session.LastError);
                return;
            }

            RefreshStatus();
        }

        /// <summary>Only for a window that never got its owner; a running session is ended through RequestServerStop instead.</summary>
        private void KillSpawnedServer()
        {
            Process process = _serverProcess;
            _serverProcess = null;
            if (process == null || ProcessHasExited(process))
            {
                return;
            }

            try
            {
                process.Kill();
            }
            catch (Exception ex)
            {
                _log.Warn("Could not close the server window: " + ex.Message);
            }
        }

        private static bool ProcessHasExited(Process process)
        {
            try
            {
                return process.HasExited;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static string SafeExitCode(Process process)
        {
            try
            {
                return process.ExitCode.ToString();
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static string ReadGameVersion()
        {
            try
            {
                return Game.Version.current.shortVersion;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string Summarize(BuildCommand command)
        {
            var builder = new StringBuilder();
            int shown = 0;
            foreach (DefinitionData definition in command.Definitions)
            {
                if (shown++ == 4)
                {
                    builder.Append(", ...");
                    break;
                }

                if (shown > 1)
                {
                    builder.Append(", ");
                }

                builder.Append(definition);
            }

            return builder.ToString();
        }

        private static string SafeServerName(string playerName)
        {
            string name = (playerName ?? string.Empty).Trim();
            name = name.Length == 0 ? "Server" : name + "'s server";
            var builder = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                builder.Append(c == '"' ? '\'' : c);
            }

            return builder.ToString();
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "'") + "\"";
        }

        private static string Base64(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private void Note(string line)
        {
            _log.Info(line);
            _recent.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + line);
            while (_recent.Count > RecentLineLimit)
            {
                _recent.RemoveAt(0);
            }

            RefreshStatus();
        }

        private void RefreshStatus()
        {
            var builder = new StringBuilder();
            if (IsWaitingForSpawnedServer)
            {
                builder.Append("Starting server window on port ").Append(_spawnedPort).Append("...");
            }
            else
            {
                switch (Session.State)
                {
                    case SessionState.Offline:
                        builder.Append("Offline");
                        break;
                    case SessionState.Failed:
                        builder.Append("Disconnected: ").Append(Session.LastError);
                        break;
                    case SessionState.Connected:
                        builder.Append(IsHostingLocally ? "Hosting" : "Connected").Append(" - ").Append(Session.ServerName)
                            .Append(Session.IsOwner ? " (you are the host)" : "");
                        break;
                    default:
                        builder.Append(Session.State);
                        break;
                }
            }

            if (Session.State == SessionState.Connected && Session.Players.Count > 0)
            {
                builder.Append('\n').Append("Players (").Append(Session.Players.Count).Append("): ");
                for (int i = 0; i < Session.Players.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(Session.Players[i]);
                }
            }

            if (Session.State == SessionState.Connected && WorldSync != null && WorldSync.StatusLine.Length > 0)
            {
                builder.Append('\n').Append(WorldSync.StatusLine);
            }

            if (Session.State == SessionState.Connected)
            {
                Sync.BuildReplaySystem replay = Replay;
                builder.Append('\n').Append("Build sync: sent ").Append(_buildsSent).Append(", received ").Append(_buildsReceived);
                if (replay != null)
                {
                    builder.Append(", applied ").Append(replay.ReplayedCount).Append(", failed ").Append(replay.FailedCount).Append(", queued ").Append(replay.QueueLength);
                }

                builder.Append("; city settings: sent ").Append(_stateSent).Append(", received ").Append(_stateReceived);
            }

            if (_recent.Count > 0)
            {
                builder.Append('\n');
                foreach (string line in _recent)
                {
                    builder.Append('\n').Append(line);
                }
            }

            StatusText = builder.ToString();
        }

        private sealed class LogAdapter : ISessionLog
        {
            private readonly ILog _log;

            public LogAdapter(ILog log)
            {
                _log = log;
            }

            public void Info(string message) => _log.Info(message);

            public void Warn(string message) => _log.Warn(message);

            public void Error(string message) => _log.Error(message);
        }
    }
}
