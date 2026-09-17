using System;
using System.Collections.Generic;
using Multiplayer.Core.Protocol;
using Multiplayer.Core.Transport;

namespace Multiplayer.Core.Session
{
    /// <summary>Everything a client needs to know about the local player and the tolerances it should apply.</summary>
    public sealed class ClientConfig
    {
        public string PlayerName = "Player";

        public string Password = string.Empty;

        /// <summary>Present this to become the session owner (the hosting player). Empty for ordinary players.</summary>
        public string OwnerKey = string.Empty;

        public string ModVersion = ProtocolConstants.ModVersion;

        public string GameVersion = string.Empty;

        /// <summary>Enabled mods as the game reports them; the server compares players against the host.</summary>
        public List<string> Mods = new List<string>();

        /// <summary>Name of the active playset, shown to others when their mods differ from the owner's.</summary>
        public string Playset = string.Empty;

        public int HeartbeatIntervalMs = 2000;

        /// <summary>Silence tolerated before the link counts as dead; heartbeats go every 2 s, so this rides out a stalled window or a long game frame.</summary>
        public int TimeoutMs = 30000;

        /// <summary>Give up waiting for the server's verdict after this long.</summary>
        public int HandshakeTimeoutMs = 8000;

        /// <summary>Give up if the socket has not opened in time.</summary>
        public int ConnectTimeoutMs = 8000;
    }

    /// <summary>
    /// What every game instance runs, the hosting player's included. Talks to a <see cref="ServerSession"/>.
    /// Drive it from one thread by calling <see cref="Update"/> every frame with a monotonic clock.
    /// All events fire on that thread, inside Update, so game code can touch ECS state from them.
    /// </summary>
    public sealed class ClientSession
    {
        private const int MaxEventsPerUpdate = 512;

        private sealed class Download
        {
            public WorldInfo Info;
            public byte[] Buffer;
            public long Received;
        }

        private readonly ISessionLog _log;
        private readonly Dictionary<int, PlayerInfo> _players = new Dictionary<int, PlayerInfo>();
        private readonly List<PlayerInfo> _playerSnapshot = new List<PlayerInfo>();

        private ITransport _transport;
        private long _nowMs;
        private long _phaseStartedMs;
        private long _lastSentMs;
        private long _lastReceivedMs;
        private float _simulationSpeed = 1f;
        private bool _playerSnapshotDirty = true;
        private Download _download;

        public ClientConfig Config { get; }

        public SessionState State { get; private set; } = SessionState.Offline;

        public int LocalPlayerId { get; private set; }

        /// <summary>True when the server accepted this client's owner key.</summary>
        public bool IsOwner { get; private set; }

        public string ServerName { get; private set; } = string.Empty;

        /// <summary>Why the last session ended, when it ended in <see cref="SessionState.Failed"/>.</summary>
        public string LastError { get; private set; } = string.Empty;

        /// <summary>Last simulation speed the server announced (0 = paused).</summary>
        public float SimulationSpeed => _simulationSpeed;

        /// <summary>The world the server currently holds, or null when it has none (or we have not heard yet).</summary>
        public WorldInfo ServerWorld { get; private set; }

        /// <summary>True once the server has told us whether it holds a world.</summary>
        public bool ServerWorldKnown { get; private set; }

        public bool IsDownloadingWorld => _download != null;

        public bool IsUploadingWorld { get; private set; }

        public bool IsOnline => State != SessionState.Offline && State != SessionState.Failed;

        public IReadOnlyList<PlayerInfo> Players
        {
            get
            {
                if (_playerSnapshotDirty)
                {
                    _playerSnapshot.Clear();
                    foreach (PlayerInfo player in _players.Values)
                    {
                        _playerSnapshot.Add(player);
                    }

                    _playerSnapshot.Sort((a, b) => a.PlayerId.CompareTo(b.PlayerId));
                    _playerSnapshotDirty = false;
                }

                return _playerSnapshot;
            }
        }

        public event Action<SessionState> StateChanged;

        public event Action<PlayerInfo> PlayerJoined;

        public event Action<PlayerInfo, string> PlayerLeft;

        /// <summary>Chat from anyone, the local player and the server console included.</summary>
        public event Action<PlayerInfo, string> ChatReceived;

        /// <summary>The server announced the simulation speed.</summary>
        public event Action<float> SimulationSpeedReceived;

        /// <summary>A gameplay command from another player that this game instance must apply.</summary>
        public event Action<GameplayCommandMessage> GameplayCommandReceived;

        /// <summary>The server told us which world it holds (null: none).</summary>
        public event Action<WorldInfo> WorldInfoReceived;

        /// <summary>Bytes received so far and the total, while a download runs.</summary>
        public event Action<long, long> WorldDownloadProgress;

        /// <summary>The whole world arrived and its hash checked out.</summary>
        public event Action<WorldSnapshot> WorldDownloaded;

        public event Action<string> WorldDownloadFailed;

        /// <summary>Accepted (with the new revision) or rejected with a reason.</summary>
        public event Action<bool, int, string> WorldUploadFinished;

        public ClientSession(ClientConfig config, ISessionLog log)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            _log = log ?? NullSessionLog.Instance;
        }

        // ------------------------------------------------------------------ lifecycle

        /// <summary>Start joining. The transport must already be connecting; the handshake is sent once the socket opens.</summary>
        public void Join(ITransport clientTransport, long nowMs)
        {
            if (clientTransport == null)
            {
                throw new ArgumentNullException(nameof(clientTransport));
            }

            if (IsOnline)
            {
                throw new InvalidOperationException("Already in a session; leave it first");
            }

            _nowMs = nowMs;
            _transport = clientTransport;
            _phaseStartedMs = nowMs;
            LastError = string.Empty;
            _log.Info("Connecting...");
            SetState(SessionState.Connecting);
        }

        public void Leave(string reason)
        {
            if (_transport == null)
            {
                return;
            }

            reason = string.IsNullOrEmpty(reason) ? "Left" : reason;
            if (State == SessionState.Connected || State == SessionState.Handshaking)
            {
                Send(new DisconnectMessage { Reason = reason });
                _transport.Disconnect(TransportIds.ServerConnectionId, reason);
            }

            _log.Info("Left session: " + reason);
            Teardown();
            SetState(SessionState.Offline);
        }

        /// <summary>Pump the transport and run timers. Call once per frame from the game thread.</summary>
        public void Update(long nowMs)
        {
            _nowMs = nowMs;
            if (_transport == null)
            {
                return;
            }

            int processed = 0;
            while (processed < MaxEventsPerUpdate && _transport != null && _transport.TryDequeue(out TransportEvent transportEvent))
            {
                processed++;
                HandleEvent(transportEvent);
            }

            if (_transport != null)
            {
                Maintenance();
            }
        }

        // ------------------------------------------------------------------ actions

        public void SendChat(string text)
        {
            if (State != SessionState.Connected)
            {
                return;
            }

            text = SessionText.SanitizeChat(text);
            if (text.Length > 0)
            {
                Send(new ChatMessage { PlayerId = LocalPlayerId, PlayerName = LocalName(), Text = text });
            }
        }

        /// <summary>The local player changed the simulation speed. The server's answer arrives through <see cref="SimulationSpeedReceived"/>.</summary>
        public void SubmitSimulationSpeed(float speed)
        {
            if (State == SessionState.Connected)
            {
                Send(new SimulationSpeedMessage { Speed = speed });
            }
        }

        /// <summary>Relay a gameplay action to everyone else.</summary>
        public void SendGameplayCommand(string kind, byte[] payload)
        {
            if (State == SessionState.Connected)
            {
                Send(new GameplayCommandMessage { Kind = kind ?? string.Empty, OriginPlayerId = LocalPlayerId, Payload = payload ?? new byte[0] });
            }
        }

        /// <summary>Owner only: ask the server to shut down and tell everyone why. Ignored by the server otherwise.</summary>
        public void RequestServerStop(string reason)
        {
            if (State == SessionState.Connected && IsOwner)
            {
                Send(new ServerControlMessage { Action = ServerControlAction.Stop, Text = reason ?? string.Empty });
            }
        }

        /// <summary>Ask the server to stream the world it announced. Progress and completion arrive through events.</summary>
        public bool RequestWorld()
        {
            if (State != SessionState.Connected || ServerWorld == null || _download != null)
            {
                return false;
            }

            if (ServerWorld.Size < 0 || ServerWorld.Size > ProtocolConstants.MaxWorldBytes)
            {
                WorldDownloadFailed?.Invoke("World size " + ServerWorld.Size + " is out of range");
                return false;
            }

            _download = new Download { Info = ServerWorld.Clone(), Buffer = new byte[ServerWorld.Size], Received = 0 };
            _log.Info("Requesting world " + ServerWorld);
            Send(new WorldRequestMessage { Revision = ServerWorld.Revision });
            return true;
        }

        /// <summary>
        /// The player whose game does the automatic saves: the owner when one is connected, otherwise the
        /// longest-connected player (lowest id). Anyone may save by hand.
        /// </summary>
        public bool IsLeader
        {
            get
            {
                if (State != SessionState.Connected)
                {
                    return false;
                }

                if (IsOwner)
                {
                    return true;
                }

                int lowest = int.MaxValue;
                foreach (PlayerInfo player in Players)
                {
                    if (player.IsOwner)
                    {
                        return false;
                    }

                    if (player.PlayerId < lowest)
                    {
                        lowest = player.PlayerId;
                    }
                }

                return lowest == LocalPlayerId;
            }
        }

        /// <summary>Replace the server's world (any player may). The whole payload is queued at once; the verdict arrives through <see cref="WorldUploadFinished"/>.</summary>
        public bool UploadWorld(string saveName, string cityName, string guid, byte[] data)
        {
            if (State != SessionState.Connected || IsUploadingWorld || data == null)
            {
                return false;
            }

            if (data.Length == 0 || data.Length > ProtocolConstants.MaxWorldBytes)
            {
                WorldUploadFinished?.Invoke(false, 0, "World size " + data.Length + " is out of range");
                return false;
            }

            IsUploadingWorld = true;
            string hash = WorldHash.Sha256Hex(data);
            Send(new WorldUploadBeginMessage { Size = data.Length, Sha256 = hash, Guid = guid ?? string.Empty, SaveName = saveName ?? string.Empty, CityName = cityName ?? string.Empty });
            for (long offset = 0; offset < data.Length; offset += ProtocolConstants.WorldChunkBytes)
            {
                int length = (int)Math.Min(ProtocolConstants.WorldChunkBytes, data.Length - offset);
                var slice = new byte[length];
                Buffer.BlockCopy(data, (int)offset, slice, 0, length);
                Send(new WorldChunkMessage { Revision = 0, Offset = offset, TotalSize = data.Length, Data = slice });
            }

            Send(new WorldUploadEndMessage());
            _log.Info("Uploading world '" + saveName + "' (" + WorldInfo.FormatSize(data.Length) + ")");
            return true;
        }

        // ------------------------------------------------------------------ events

        private void HandleEvent(TransportEvent transportEvent)
        {
            switch (transportEvent.Type)
            {
                case TransportEventType.Connected:
                    _lastSentMs = _nowMs;
                    _lastReceivedMs = _nowMs;
                    _phaseStartedMs = _nowMs;
                    Send(new HandshakeRequestMessage
                    {
                        ModVersion = Config.ModVersion,
                        GameVersion = Config.GameVersion ?? string.Empty,
                        PlayerName = SessionText.SanitizeName(Config.PlayerName),
                        Password = Config.Password ?? string.Empty,
                        OwnerKey = Config.OwnerKey ?? string.Empty,
                        Mods = ModListCompare.Normalize(Config.Mods),
                        Playset = (Config.Playset ?? string.Empty).Trim(),
                    });
                    SetState(SessionState.Handshaking);
                    break;

                case TransportEventType.Disconnected:
                    if (State == SessionState.Connecting)
                    {
                        Fail("Could not connect: " + transportEvent.Reason);
                    }
                    else if (IsOnline)
                    {
                        Fail("Connection lost: " + transportEvent.Reason);
                    }

                    break;

                case TransportEventType.Data:
                    _lastReceivedMs = _nowMs;
                    HandleData(transportEvent.Data);
                    break;
            }
        }

        private void HandleData(byte[] data)
        {
            NetMessage message;
            try
            {
                message = MessageCodec.Decode(data);
            }
            catch (ProtocolException ex)
            {
                Fail("Server sent a malformed message: " + ex.Message);
                return;
            }

            switch (message)
            {
                case HandshakeResponseMessage response:
                    if (State != SessionState.Handshaking)
                    {
                        break;
                    }

                    ServerName = response.ServerName ?? string.Empty;
                    if (response.Accepted)
                    {
                        LocalPlayerId = response.PlayerId;
                        IsOwner = response.IsOwner;
                        _log.Info("Joined '" + ServerName + "' as player " + LocalPlayerId + (IsOwner ? " (owner)" : ""));
                        SetState(SessionState.Connected);
                    }
                    else
                    {
                        Fail("Rejected: " + response.Reason);
                    }

                    break;

                case PlayerListMessage list:
                    ApplyPlayerList(list);
                    break;

                case SimulationSpeedMessage speed:
                    _simulationSpeed = speed.Speed;
                    SimulationSpeedReceived?.Invoke(speed.Speed);
                    break;

                case ChatMessage chat:
                {
                    PlayerInfo sender = _players.TryGetValue(chat.PlayerId, out PlayerInfo known)
                        ? known
                        : new PlayerInfo(chat.PlayerId, chat.PlayerName, false);
                    ChatReceived?.Invoke(sender, chat.Text ?? string.Empty);
                    break;
                }

                case GameplayCommandMessage command:
                    GameplayCommandReceived?.Invoke(command);
                    break;

                case WorldInfoMessage worldInfo:
                    ServerWorld = worldInfo.HasWorld ? worldInfo.Info : null;
                    ServerWorldKnown = true;
                    _log.Info(ServerWorld != null ? "Server holds world " + ServerWorld : "Server holds no world");
                    WorldInfoReceived?.Invoke(ServerWorld);
                    break;

                case WorldChunkMessage chunk:
                    HandleWorldChunk(chunk);
                    break;

                case WorldUploadResultMessage result:
                    IsUploadingWorld = false;
                    _log.Info(result.Accepted ? "World upload accepted as revision " + result.Revision : "World upload rejected: " + result.Reason);
                    WorldUploadFinished?.Invoke(result.Accepted, result.Revision, result.Reason ?? string.Empty);
                    break;

                case DisconnectMessage disconnect:
                    Fail(string.IsNullOrEmpty(disconnect.Reason) ? "Disconnected by server" : disconnect.Reason);
                    break;

                case HeartbeatMessage _:
                    break;

                default:
                    _log.Warn("Ignoring unexpected " + message.Type + " from server");
                    break;
            }
        }

        private void HandleWorldChunk(WorldChunkMessage chunk)
        {
            Download download = _download;
            if (download == null)
            {
                return;
            }

            if (chunk.Revision != download.Info.Revision || chunk.TotalSize != download.Buffer.Length
                || chunk.Offset != download.Received || chunk.Offset + chunk.Data.Length > download.Buffer.Length)
            {
                _download = null;
                _log.Warn("World download aborted: unexpected chunk (revision " + chunk.Revision + ", offset " + chunk.Offset + ")");
                WorldDownloadFailed?.Invoke("Download interrupted; the server's world changed or chunks arrived out of order");
                return;
            }

            Buffer.BlockCopy(chunk.Data, 0, download.Buffer, (int)chunk.Offset, chunk.Data.Length);
            download.Received += chunk.Data.Length;
            WorldDownloadProgress?.Invoke(download.Received, download.Buffer.Length);

            if (download.Received < download.Buffer.Length)
            {
                return;
            }

            _download = null;
            string hash = WorldHash.Sha256Hex(download.Buffer);
            if (!string.Equals(hash, download.Info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _log.Warn("World download corrupted (hash mismatch)");
                WorldDownloadFailed?.Invoke("Download corrupted (hash mismatch)");
                return;
            }

            _log.Info("World " + download.Info + " downloaded");
            WorldDownloaded?.Invoke(new WorldSnapshot(download.Info, download.Buffer));
        }

        private void ApplyPlayerList(PlayerListMessage list)
        {
            var incoming = new Dictionary<int, PlayerInfo>();
            foreach (PlayerListEntry entry in list.Players)
            {
                incoming[entry.PlayerId] = new PlayerInfo(entry.PlayerId, entry.Name, entry.IsOwner);
            }

            var left = new List<PlayerInfo>();
            foreach (KeyValuePair<int, PlayerInfo> existing in _players)
            {
                if (!incoming.ContainsKey(existing.Key))
                {
                    left.Add(existing.Value);
                }
            }

            var joined = new List<PlayerInfo>();
            foreach (KeyValuePair<int, PlayerInfo> entry in incoming)
            {
                if (!_players.ContainsKey(entry.Key))
                {
                    joined.Add(entry.Value);
                }
            }

            _players.Clear();
            foreach (KeyValuePair<int, PlayerInfo> entry in incoming)
            {
                _players[entry.Key] = entry.Value;
            }

            _playerSnapshotDirty = true;

            foreach (PlayerInfo player in left)
            {
                PlayerLeft?.Invoke(player, "Left");
            }

            foreach (PlayerInfo player in joined)
            {
                if (player.PlayerId != LocalPlayerId)
                {
                    PlayerJoined?.Invoke(player);
                }
            }
        }

        private void Maintenance()
        {
            switch (State)
            {
                case SessionState.Connecting:
                    if (_nowMs - _phaseStartedMs > Config.ConnectTimeoutMs)
                    {
                        Fail("Connection timed out");
                    }

                    break;

                case SessionState.Handshaking:
                    if (_nowMs - _phaseStartedMs > Config.HandshakeTimeoutMs)
                    {
                        Fail("No answer from server");
                    }

                    break;

                case SessionState.Connected:
                    if (_nowMs - _lastReceivedMs > Config.TimeoutMs)
                    {
                        Fail("Connection timed out");
                    }
                    else if (_nowMs - _lastSentMs > Config.HeartbeatIntervalMs)
                    {
                        Send(new HeartbeatMessage());
                    }

                    break;
            }
        }

        // ------------------------------------------------------------------ helpers

        private void Send(NetMessage message)
        {
            _lastSentMs = _nowMs;
            _transport.Send(TransportIds.ServerConnectionId, MessageCodec.Encode(message));
        }

        private string LocalName()
        {
            return _players.TryGetValue(LocalPlayerId, out PlayerInfo info) ? info.Name : SessionText.SanitizeName(Config.PlayerName);
        }

        private void Fail(string reason)
        {
            LastError = reason ?? "Unknown error";
            _log.Warn("Session failed: " + LastError);
            Teardown();
            SetState(SessionState.Failed);
        }

        private void Teardown()
        {
            ITransport transport = _transport;
            _transport = null;
            try
            {
                transport?.Stop();
                transport?.Dispose();
            }
            catch (Exception ex)
            {
                _log.Warn("Transport shutdown threw: " + ex.Message);
            }

            _players.Clear();
            _playerSnapshotDirty = true;
            LocalPlayerId = 0;
            IsOwner = false;
            _simulationSpeed = 1f;
            ServerWorld = null;
            ServerWorldKnown = false;
            _download = null;
            IsUploadingWorld = false;
        }

        private void SetState(SessionState state)
        {
            if (State == state)
            {
                return;
            }

            State = state;
            StateChanged?.Invoke(state);
        }
    }
}
