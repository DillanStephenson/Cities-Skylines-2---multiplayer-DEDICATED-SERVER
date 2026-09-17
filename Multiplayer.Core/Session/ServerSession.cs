using System;
using System.Collections.Generic;
using Multiplayer.Core.Protocol;
using Multiplayer.Core.Transport;

namespace Multiplayer.Core.Session
{
    /// <summary>Knobs for the server side.</summary>
    public sealed class ServerConfig
    {
        /// <summary>Shown to clients in the handshake and used as the chat name of the console.</summary>
        public string ServerName = "Server";

        /// <summary>Password clients must present. Empty = open session.</summary>
        public string Password = string.Empty;

        /// <summary>Secret that grants one client owner (host player) status. Empty = nobody can be owner.</summary>
        public string OwnerKey = string.Empty;

        public string ModVersion = ProtocolConstants.ModVersion;

        /// <summary>Other mod versions this server also lets in (same protocol, minor differences), for the days a release is rolling out.</summary>
        public List<string> ExtraModVersions = new List<string>();

        /// <summary>Game build clients must match exactly. Empty disables the check.</summary>
        public string GameVersion = string.Empty;

        /// <summary>Players must run the same mods as the owner (learned when the owner joins).</summary>
        public bool RequireMatchingMods = true;

        /// <summary>With the check on, the same Paradox mod at another version still matches (playsets update at their own pace on each PC).</summary>
        public bool IgnoreModVersions = true;

        /// <summary>Extra text for rejected joiners, such as the public playset id everyone should activate.</summary>
        public string PlaysetHint = string.Empty;

        /// <summary>
        /// Whether the owner's game may stop the whole server (true for the window the game spawns next to
        /// itself; false for a standalone or dedicated server, which outlives any one player's game).
        /// </summary>
        public bool OwnerMayStop = true;

        public int MaxPlayers = 8;

        public int HeartbeatIntervalMs = 2000;

        public int TimeoutMs = 10000;

        /// <summary>Kick a socket that has not handshaken in time.</summary>
        public int HandshakeTimeoutMs = 8000;
    }

    /// <summary>
    /// The authority: accepts players, keeps the roster, relays chat, speed and gameplay commands, and holds
    /// the world. Runs in the server console process. Has no player of its own; the hosting player is just
    /// the client that presented the owner key. Drive it by calling <see cref="Update"/> from one thread.
    /// </summary>
    public sealed class ServerSession
    {
        private const int MaxEventsPerUpdate = 512;

        private sealed class Peer
        {
            public int ConnectionId;
            public int PlayerId;
            public string Name = string.Empty;
            public bool Authenticated;
            public long ConnectedAtMs;
            public long LastReceivedMs;
            public long LastSentMs;
        }

        private sealed class PendingUpload
        {
            public int ConnectionId;
            public WorldUploadBeginMessage Begin;
            public byte[] Buffer;
            public long Received;
        }

        private readonly ISessionLog _log;
        private readonly Dictionary<int, Peer> _peersByConnection = new Dictionary<int, Peer>();
        private readonly Dictionary<int, Peer> _peersByPlayer = new Dictionary<int, Peer>();
        private readonly Dictionary<int, PlayerInfo> _players = new Dictionary<int, PlayerInfo>();
        private readonly List<PlayerInfo> _playerSnapshot = new List<PlayerInfo>();

        private ITransport _transport;
        private int _nextPlayerId = ProtocolConstants.FirstPlayerId;
        private long _nowMs;
        private float _simulationSpeed = 1f;
        private bool _playerSnapshotDirty = true;
        private WorldSnapshot _world;
        private List<string> _referenceMods;
        private PendingUpload _upload;

        public ServerConfig Config { get; }

        public bool IsRunning => _transport != null;

        /// <summary>Player id of the owner, or 0 while no owner is connected.</summary>
        public int OwnerPlayerId { get; private set; }

        public float SimulationSpeed => _simulationSpeed;

        public long StartedAtMs { get; private set; }

        public long BytesIn { get; private set; }

        public long BytesOut { get; private set; }

        /// <summary>The world the server holds, or null.</summary>
        public WorldSnapshot World => _world;

        /// <summary>Mod list every player must match, learned from the owner; null until an owner has joined.</summary>
        public IReadOnlyList<string> ReferenceMods => _referenceMods;

        /// <summary>Name of the owner's playset, told to anyone rejected over mods. Empty until an owner has joined.</summary>
        public string ReferencePlayset { get; private set; } = string.Empty;

        /// <summary>True when the reference follows a published Paradox playset; the owner's own list then no longer replaces it.</summary>
        public bool ReferenceLocked { get; private set; }

        /// <summary>Set by the update check when a newer release exists: told to the host on join and to anyone whose mod is newer than this server.</summary>
        public string UpdateNotice { get; set; } = string.Empty;

        public bool IsReceivingWorld => _upload != null;

        public long UploadReceivedBytes => _upload != null ? _upload.Received : 0;

        public long UploadTotalBytes => _upload != null ? _upload.Buffer.Length : 0;

        /// <summary>Sockets that have connected but not yet handshaken.</summary>
        public int PendingSockets
        {
            get
            {
                int count = 0;
                foreach (Peer peer in _peersByConnection.Values)
                {
                    if (!peer.Authenticated)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

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

        public event Action<PlayerInfo> PlayerJoined;

        public event Action<PlayerInfo, string> PlayerLeft;

        /// <summary>Chat from a player (already relayed to everyone).</summary>
        public event Action<PlayerInfo, string> ChatReceived;

        /// <summary>Speed changed; the second argument is the player who asked, or null when the console did.</summary>
        public event Action<float, PlayerInfo> SimulationSpeedChanged;

        /// <summary>A gameplay command passed through (already relayed to the other players).</summary>
        public event Action<GameplayCommandMessage> GameplayCommandRelayed;

        /// <summary>A new world was accepted from the owner (or set from storage while running).</summary>
        public event Action<WorldSnapshot> WorldChanged;

        /// <summary>The world is being streamed to a player.</summary>
        public event Action<PlayerInfo, WorldInfo> WorldSent;

        /// <summary>The owner's mod list became the reference.</summary>
        public event Action<IReadOnlyList<string>> ReferenceModsChanged;

        public event Action<string> Stopped;

        public ServerSession(ServerConfig config, ISessionLog log)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            _log = log ?? NullSessionLog.Instance;
        }

        // ------------------------------------------------------------------ lifecycle

        /// <summary>Begin accepting players. The transport must already be listening.</summary>
        public void Start(ITransport listeningTransport, long nowMs)
        {
            if (listeningTransport == null)
            {
                throw new ArgumentNullException(nameof(listeningTransport));
            }

            if (IsRunning)
            {
                throw new InvalidOperationException("Server already running");
            }

            _transport = listeningTransport;
            _nowMs = nowMs;
            StartedAtMs = nowMs;
            _log.Info("Server '" + Config.ServerName + "' started" + (string.IsNullOrEmpty(Config.Password) ? " (no password)" : " (password set)")
                + (_world != null ? ", holding world " + _world.Info : ", no world yet"));
        }

        /// <summary>Tell everyone, close every socket, stop listening.</summary>
        public void Stop(string reason)
        {
            if (!IsRunning)
            {
                return;
            }

            reason = string.IsNullOrEmpty(reason) ? "Server stopped" : reason;
            var notice = new DisconnectMessage { Reason = reason };
            foreach (Peer peer in _peersByConnection.Values)
            {
                SendToPeer(peer, notice);
                _transport.Disconnect(peer.ConnectionId, reason);
            }

            ITransport transport = _transport;
            _transport = null;
            try
            {
                transport.Stop();
                transport.Dispose();
            }
            catch (Exception ex)
            {
                _log.Warn("Transport shutdown threw: " + ex.Message);
            }

            _peersByConnection.Clear();
            _peersByPlayer.Clear();
            _players.Clear();
            _playerSnapshotDirty = true;
            _upload = null;
            OwnerPlayerId = 0;
            _log.Info("Server stopped: " + reason);
            Stopped?.Invoke(reason);
        }

        /// <summary>Pump the transport and run timers. Call regularly (every frame or every few ms).</summary>
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
                if (_transport == null)
                {
                    // A control message stopped the server mid-drain.
                    return;
                }
            }

            if (_transport != null)
            {
                Maintenance();
            }
        }

        // ------------------------------------------------------------------ world storage

        /// <summary>Install a world (typically read back from disk at startup). Announced to connected players if any.</summary>
        public void SetWorld(WorldSnapshot snapshot)
        {
            _world = snapshot;
            if (IsRunning)
            {
                Broadcast(BuildWorldInfo(), -1);
            }
        }

        /// <summary>Install the reference mod list (typically read back from disk at startup).</summary>
        public void SetReferenceMods(IEnumerable<string> mods, string playset = null, bool locked = false)
        {
            _referenceMods = mods == null ? null : ModListCompare.Normalize(mods);
            ReferencePlayset = (playset ?? string.Empty).Trim();
            ReferenceLocked = locked && _referenceMods != null;
        }

        // ------------------------------------------------------------------ console actions

        /// <summary>A line from the server to one player only (welcome text, warnings).</summary>
        public void Tell(int playerId, string text)
        {
            if (!IsRunning || !_peersByPlayer.TryGetValue(playerId, out Peer peer))
            {
                return;
            }

            text = SessionText.SanitizeChat(text);
            if (text.Length == 0)
            {
                return;
            }

            SendToPeer(peer, new ChatMessage { PlayerId = ProtocolConstants.ServerPlayerId, PlayerName = Config.ServerName, Text = text });
        }

        /// <summary>Chat from the console itself.</summary>
        public void Say(string text)
        {
            if (!IsRunning)
            {
                return;
            }

            text = SessionText.SanitizeChat(text);
            if (text.Length == 0)
            {
                return;
            }

            Broadcast(new ChatMessage { PlayerId = ProtocolConstants.ServerPlayerId, PlayerName = Config.ServerName, Text = text }, -1);
        }

        /// <summary>The console decides the speed.</summary>
        public void SetSimulationSpeed(float speed)
        {
            if (!IsRunning)
            {
                return;
            }

            _simulationSpeed = speed;
            Broadcast(new SimulationSpeedMessage { Speed = speed }, -1);
            SimulationSpeedChanged?.Invoke(speed, null);
        }

        public bool KickPlayer(int playerId, string reason)
        {
            if (!_peersByPlayer.TryGetValue(playerId, out Peer peer))
            {
                return false;
            }

            reason = string.IsNullOrEmpty(reason) ? "Kicked" : reason;
            SendToPeer(peer, new DisconnectMessage { Reason = reason });
            Kick(peer, reason);
            return true;
        }

        public PlayerInfo FindPlayer(string idOrName)
        {
            idOrName = (idOrName ?? string.Empty).Trim();
            if (int.TryParse(idOrName, out int id) && _players.TryGetValue(id, out PlayerInfo byId))
            {
                return byId;
            }

            foreach (PlayerInfo player in _players.Values)
            {
                if (string.Equals(player.Name, idOrName, StringComparison.OrdinalIgnoreCase))
                {
                    return player;
                }
            }

            return null;
        }

        // ------------------------------------------------------------------ events

        private void HandleEvent(TransportEvent transportEvent)
        {
            switch (transportEvent.Type)
            {
                case TransportEventType.Connected:
                    _peersByConnection[transportEvent.ConnectionId] = new Peer
                    {
                        ConnectionId = transportEvent.ConnectionId,
                        ConnectedAtMs = _nowMs,
                        LastReceivedMs = _nowMs,
                        LastSentMs = _nowMs,
                    };
                    _log.Info("Socket " + transportEvent.ConnectionId + " connected, awaiting handshake");
                    break;

                case TransportEventType.Disconnected:
                    RemovePeer(transportEvent.ConnectionId, transportEvent.Reason);
                    break;

                case TransportEventType.Data:
                    if (_peersByConnection.TryGetValue(transportEvent.ConnectionId, out Peer peer))
                    {
                        peer.LastReceivedMs = _nowMs;
                        BytesIn += transportEvent.Data.Length;
                        HandleData(peer, transportEvent.Data);
                    }

                    break;
            }
        }

        private void HandleData(Peer peer, byte[] data)
        {
            NetMessage message;
            try
            {
                message = MessageCodec.Decode(data);
            }
            catch (ProtocolException ex)
            {
                _log.Warn("Dropping socket " + peer.ConnectionId + ": " + ex.Message);
                Kick(peer, "Malformed message");
                return;
            }

            if (!peer.Authenticated)
            {
                if (message is HandshakeRequestMessage request)
                {
                    HandleHandshake(peer, request);
                }
                else if (message is DisconnectMessage)
                {
                    RemovePeer(peer.ConnectionId, "Left before handshake");
                }

                return;
            }

            switch (message)
            {
                case HeartbeatMessage _:
                    break;

                case DisconnectMessage disconnect:
                    RemovePeer(peer.ConnectionId, string.IsNullOrEmpty(disconnect.Reason) ? "Left" : disconnect.Reason);
                    break;

                case ChatMessage chat:
                {
                    string text = SessionText.SanitizeChat(chat.Text);
                    if (text.Length == 0)
                    {
                        break;
                    }

                    Broadcast(new ChatMessage { PlayerId = peer.PlayerId, PlayerName = peer.Name, Text = text }, -1);
                    if (_players.TryGetValue(peer.PlayerId, out PlayerInfo sender))
                    {
                        ChatReceived?.Invoke(sender, text);
                    }

                    break;
                }

                case SimulationSpeedMessage speed:
                    _simulationSpeed = speed.Speed;
                    Broadcast(new SimulationSpeedMessage { Speed = speed.Speed }, -1);
                    SimulationSpeedChanged?.Invoke(speed.Speed, _players.TryGetValue(peer.PlayerId, out PlayerInfo requester) ? requester : null);
                    break;

                case GameplayCommandMessage command:
                    command.OriginPlayerId = peer.PlayerId;
                    Broadcast(command, peer.ConnectionId);
                    GameplayCommandRelayed?.Invoke(command);
                    break;

                case ServerControlMessage control:
                    if (peer.PlayerId != OwnerPlayerId)
                    {
                        _log.Warn("Ignoring server control from non-owner player " + peer.PlayerId + " '" + peer.Name + "'");
                        break;
                    }

                    if (control.Action == ServerControlAction.Stop)
                    {
                        if (!Config.OwnerMayStop)
                        {
                            _log.Info("Owner asked to stop (" + control.Text + "); a standalone server keeps running");
                            break;
                        }

                        _log.Info("Owner asked to stop: " + control.Text);
                        Stop(string.IsNullOrEmpty(control.Text) ? "Host closed the session" : control.Text);
                    }

                    break;

                case WorldRequestMessage request:
                    HandleWorldRequest(peer, request);
                    break;

                case WorldUploadBeginMessage begin:
                    HandleUploadBegin(peer, begin);
                    break;

                case WorldChunkMessage chunk:
                    HandleUploadChunk(peer, chunk);
                    break;

                case WorldUploadEndMessage _:
                    HandleUploadEnd(peer);
                    break;

                case HandshakeRequestMessage _:
                    break;

                default:
                    _log.Warn("Ignoring unexpected " + message.Type + " from player " + peer.PlayerId);
                    break;
            }
        }

        // ------------------------------------------------------------------ handshake

        private void HandleHandshake(Peer peer, HandshakeRequestMessage request)
        {
            List<string> mods = ModListCompare.Normalize(request.Mods);
            string rejection = Validate(request, mods, out bool isOwner);
            if (rejection != null)
            {
                _log.Info("Rejected socket " + peer.ConnectionId + " ('" + request.PlayerName + "'): " + rejection);
                SendToPeer(peer, new HandshakeResponseMessage { Accepted = false, Reason = rejection, ServerName = Config.ServerName });
                Kick(peer, rejection);
                return;
            }

            peer.Authenticated = true;
            peer.PlayerId = _nextPlayerId++;
            peer.Name = UniqueName(SessionText.SanitizeName(request.PlayerName));
            _peersByPlayer[peer.PlayerId] = peer;

            var info = new PlayerInfo(peer.PlayerId, peer.Name, isOwner);
            _players[peer.PlayerId] = info;
            _playerSnapshotDirty = true;
            string ownerWarning = null;
            if (isOwner)
            {
                OwnerPlayerId = peer.PlayerId;
                string playset = (request.Playset ?? string.Empty).Trim();
                if (ReferenceLocked)
                {
                    // The published playset is the reference; the owner is told when their own game differs from it.
                    string difference = ModListCompare.Describe(_referenceMods, mods, null, Config.IgnoreModVersions);
                    if (difference != null)
                    {
                        _log.Warn("Owner's mods differ from the linked playset: " + difference);
                        ownerWarning = "Your mods differ from the published playset '" + ReferencePlayset + "' (" + difference + "). Publish your playset changes in Paradox Mods so everyone gets them.";
                    }
                }
                else
                {
                    bool listChanged = _referenceMods == null || ModListCompare.Describe(_referenceMods, mods, null) != null;
                    if (listChanged || !string.Equals(ReferencePlayset, playset, StringComparison.Ordinal))
                    {
                        _referenceMods = mods;
                        ReferencePlayset = playset;
                        _log.Info("Reference mod list set from the owner: " + mods.Count + " mods" + (playset.Length > 0 ? ", playset '" + playset + "'" : ""));
                        ReferenceModsChanged?.Invoke(_referenceMods);
                    }
                }
            }

            SendToPeer(peer, new HandshakeResponseMessage { Accepted = true, PlayerId = peer.PlayerId, IsOwner = isOwner, ServerName = Config.ServerName });
            SendToPeer(peer, new SimulationSpeedMessage { Speed = _simulationSpeed });
            SendToPeer(peer, BuildWorldInfo());
            Broadcast(BuildPlayerList(), -1);
            if (ownerWarning != null)
            {
                SendToPeer(peer, new ChatMessage { PlayerId = ProtocolConstants.ServerPlayerId, PlayerName = Config.ServerName, Text = SessionText.SanitizeChat(ownerWarning) });
            }

            if (isOwner && UpdateNotice.Length > 0)
            {
                SendToPeer(peer, new ChatMessage { PlayerId = ProtocolConstants.ServerPlayerId, PlayerName = Config.ServerName, Text = SessionText.SanitizeChat(UpdateNotice) });
            }

            _log.Info("Player " + peer.PlayerId + " '" + peer.Name + "' joined" + (isOwner ? " as owner" : "") + " with " + mods.Count + " mods");
            PlayerJoined?.Invoke(info);
        }

        private string Validate(HandshakeRequestMessage request, List<string> mods, out bool isOwner)
        {
            isOwner = false;

            if (request.ProtocolVersion != ProtocolConstants.ProtocolVersion)
            {
                return "Incompatible mod protocol (server " + ProtocolConstants.ProtocolVersion + ", you " + request.ProtocolVersion + ")";
            }

            if (!string.Equals(request.ModVersion, Config.ModVersion, StringComparison.Ordinal) && !Config.ExtraModVersions.Contains(request.ModVersion ?? string.Empty))
            {
                string advice = GitHubReleases.IsNewer(request.ModVersion, Config.ModVersion)
                    ? ". This server needs updating" + (UpdateNotice.Length > 0 ? ": " + UpdateNotice : "")
                    : ". Update the mod";
                return "Mod version mismatch (server " + Config.ModVersion + ", you " + request.ModVersion + ")" + advice;
            }

            // A server that knows its game build requires clients to state the same one; an empty answer does not pass.
            if (!string.IsNullOrEmpty(Config.GameVersion)
                && !string.Equals(request.GameVersion ?? string.Empty, Config.GameVersion, StringComparison.Ordinal))
            {
                return "Game version mismatch (server " + Config.GameVersion + ", you " + (string.IsNullOrEmpty(request.GameVersion) ? "unknown" : request.GameVersion) + ")";
            }

            if (!string.IsNullOrEmpty(Config.Password) && !string.Equals(request.Password ?? string.Empty, Config.Password, StringComparison.Ordinal))
            {
                return "Wrong password";
            }

            string presentedKey = request.OwnerKey ?? string.Empty;
            if (presentedKey.Length > 0)
            {
                if (string.IsNullOrEmpty(Config.OwnerKey) || !string.Equals(presentedKey, Config.OwnerKey, StringComparison.Ordinal))
                {
                    return "Wrong owner key";
                }

                if (OwnerPlayerId != 0)
                {
                    return "The owner is already connected";
                }

                isOwner = true;
            }

            if (Config.RequireMatchingMods && !isOwner && _referenceMods != null)
            {
                string difference = ModListCompare.Describe(_referenceMods, mods, null, Config.IgnoreModVersions);
                if (difference != null)
                {
                    return difference + PlaysetAdvice();
                }
            }

            if (_players.Count >= Math.Max(1, Math.Min(Config.MaxPlayers, ProtocolConstants.MaxPlayers)))
            {
                return "Session is full";
            }

            return null;
        }

        /// <summary>". The host plays with the playset 'X' (hint); activate it in Paradox Mods and restart the game", or nothing known.</summary>
        private string PlaysetAdvice()
        {
            string hint = (Config.PlaysetHint ?? string.Empty).Trim();
            if (ReferencePlayset.Length == 0 && hint.Length == 0)
            {
                return string.Empty;
            }

            string text = ". The host plays with the playset";
            if (ReferencePlayset.Length > 0)
            {
                text += " '" + ReferencePlayset + "'";
            }

            if (hint.Length > 0)
            {
                text += " (" + hint + ")";
            }

            return text + "; activate it in Paradox Mods and restart the game";
        }

        // ------------------------------------------------------------------ world transfer

        private WorldInfoMessage BuildWorldInfo()
        {
            return _world == null
                ? new WorldInfoMessage { HasWorld = false }
                : new WorldInfoMessage { HasWorld = true, Info = _world.Info.Clone() };
        }

        private void HandleWorldRequest(Peer peer, WorldRequestMessage request)
        {
            if (_world == null)
            {
                SendToPeer(peer, BuildWorldInfo());
                return;
            }

            if (request.Revision != _world.Info.Revision)
            {
                // They asked for an old revision; tell them what is current and let them ask again.
                SendToPeer(peer, BuildWorldInfo());
                return;
            }

            byte[] data = _world.Data;
            int revision = _world.Info.Revision;
            for (long offset = 0; offset < data.Length; offset += ProtocolConstants.WorldChunkBytes)
            {
                int length = (int)Math.Min(ProtocolConstants.WorldChunkBytes, data.Length - offset);
                var slice = new byte[length];
                Buffer.BlockCopy(data, (int)offset, slice, 0, length);
                SendToPeer(peer, new WorldChunkMessage { Revision = revision, Offset = offset, TotalSize = data.Length, Data = slice });
            }

            if (data.Length == 0)
            {
                SendToPeer(peer, new WorldChunkMessage { Revision = revision, Offset = 0, TotalSize = 0, Data = new byte[0] });
            }

            _log.Info("Sending world " + _world.Info + " to player " + peer.PlayerId + " '" + peer.Name + "'");
            if (_players.TryGetValue(peer.PlayerId, out PlayerInfo info))
            {
                WorldSent?.Invoke(info, _world.Info);
            }
        }

        private void HandleUploadBegin(Peer peer, WorldUploadBeginMessage begin)
        {
            // Any player may save the city to the server; the newest upload is the shared copy.
            if (_upload != null && _upload.ConnectionId != peer.ConnectionId)
            {
                SendToPeer(peer, new WorldUploadResultMessage { Accepted = false, Reason = "Someone else is uploading right now; try again in a moment" });
                return;
            }

            if (begin.Size <= 0 || begin.Size > ProtocolConstants.MaxWorldBytes)
            {
                SendToPeer(peer, new WorldUploadResultMessage { Accepted = false, Reason = "World size " + begin.Size + " is out of range" });
                return;
            }

            _upload = new PendingUpload
            {
                ConnectionId = peer.ConnectionId,
                Begin = begin,
                Buffer = new byte[begin.Size],
                Received = 0,
            };
            _log.Info("Receiving world '" + begin.SaveName + "' (" + WorldInfo.FormatSize(begin.Size) + ") from player " + peer.PlayerId + " '" + peer.Name + "'");
        }

        private void HandleUploadChunk(Peer peer, WorldChunkMessage chunk)
        {
            PendingUpload upload = _upload;
            if (upload == null || upload.ConnectionId != peer.ConnectionId)
            {
                return;
            }

            if (chunk.Offset != upload.Received || chunk.Offset + chunk.Data.Length > upload.Buffer.Length)
            {
                _upload = null;
                SendToPeer(peer, new WorldUploadResultMessage { Accepted = false, Reason = "Upload chunks out of order" });
                _log.Warn("Upload from the owner aborted: chunk at " + chunk.Offset + " when " + upload.Received + " expected");
                return;
            }

            Buffer.BlockCopy(chunk.Data, 0, upload.Buffer, (int)chunk.Offset, chunk.Data.Length);
            upload.Received += chunk.Data.Length;
        }

        private void HandleUploadEnd(Peer peer)
        {
            PendingUpload upload = _upload;
            if (upload == null || upload.ConnectionId != peer.ConnectionId)
            {
                return;
            }

            _upload = null;
            if (upload.Received != upload.Buffer.Length)
            {
                SendToPeer(peer, new WorldUploadResultMessage { Accepted = false, Reason = "Upload incomplete (" + upload.Received + " of " + upload.Buffer.Length + " bytes)" });
                return;
            }

            string hash = WorldHash.Sha256Hex(upload.Buffer);
            if (!string.Equals(hash, upload.Begin.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                SendToPeer(peer, new WorldUploadResultMessage { Accepted = false, Reason = "Upload corrupted (hash mismatch)" });
                return;
            }

            int revision = (_world != null ? _world.Info.Revision : 0) + 1;
            var info = new WorldInfo
            {
                Revision = revision,
                Size = upload.Buffer.Length,
                Sha256 = hash,
                Guid = upload.Begin.Guid ?? string.Empty,
                SaveName = upload.Begin.SaveName ?? string.Empty,
                CityName = upload.Begin.CityName ?? string.Empty,
                SavedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                UploaderName = peer.Name,
            };
            _world = new WorldSnapshot(info, upload.Buffer);

            SendToPeer(peer, new WorldUploadResultMessage { Accepted = true, Revision = revision });
            Broadcast(BuildWorldInfo(), -1);
            _log.Info("World updated: " + info + " from '" + peer.Name + "'");
            WorldChanged?.Invoke(_world);
        }

        // ------------------------------------------------------------------ maintenance

        private void Maintenance()
        {
            List<Peer> toKick = null;
            foreach (Peer peer in _peersByConnection.Values)
            {
                bool silentBeforeHandshake = !peer.Authenticated && _nowMs - peer.ConnectedAtMs > Config.HandshakeTimeoutMs;
                bool timedOut = _nowMs - peer.LastReceivedMs > Config.TimeoutMs;
                if (silentBeforeHandshake || timedOut)
                {
                    (toKick ?? (toKick = new List<Peer>())).Add(peer);
                    continue;
                }

                if (peer.Authenticated && _nowMs - peer.LastSentMs > Config.HeartbeatIntervalMs)
                {
                    SendToPeer(peer, new HeartbeatMessage());
                }
            }

            if (toKick != null)
            {
                foreach (Peer peer in toKick)
                {
                    string why = peer.Authenticated ? "Timed out" : "Handshake timeout";
                    SendToPeer(peer, new DisconnectMessage { Reason = why });
                    Kick(peer, why);
                }
            }
        }

        /// <summary>Close the socket and forget the peer now; the transport's later Disconnected event finds nothing to remove.</summary>
        private void Kick(Peer peer, string reason)
        {
            _transport.Disconnect(peer.ConnectionId, reason);
            RemovePeer(peer.ConnectionId, reason);
        }

        private void RemovePeer(int connectionId, string reason)
        {
            if (!_peersByConnection.TryGetValue(connectionId, out Peer peer))
            {
                return;
            }

            _peersByConnection.Remove(connectionId);
            if (_upload != null && _upload.ConnectionId == connectionId)
            {
                _upload = null;
                _log.Warn("Upload abandoned: the owner left mid-transfer");
            }

            if (!peer.Authenticated)
            {
                _log.Info("Socket " + connectionId + " gone before handshake: " + reason);
                return;
            }

            _peersByPlayer.Remove(peer.PlayerId);
            if (_players.TryGetValue(peer.PlayerId, out PlayerInfo info))
            {
                _players.Remove(peer.PlayerId);
                _playerSnapshotDirty = true;
                if (OwnerPlayerId == peer.PlayerId)
                {
                    OwnerPlayerId = 0;
                }

                _log.Info("Player " + peer.PlayerId + " '" + peer.Name + "' left: " + reason);
                Broadcast(BuildPlayerList(), -1);
                PlayerLeft?.Invoke(info, reason);
            }
        }

        private PlayerListMessage BuildPlayerList()
        {
            var message = new PlayerListMessage();
            foreach (PlayerInfo player in Players)
            {
                message.Players.Add(new PlayerListEntry { PlayerId = player.PlayerId, Name = player.Name, IsOwner = player.IsOwner });
            }

            return message;
        }

        private void Broadcast(NetMessage message, int exceptConnectionId)
        {
            byte[] bytes = MessageCodec.Encode(message);
            foreach (Peer peer in _peersByConnection.Values)
            {
                if (peer.Authenticated && peer.ConnectionId != exceptConnectionId)
                {
                    SendRaw(peer, bytes);
                }
            }
        }

        private void SendToPeer(Peer peer, NetMessage message)
        {
            SendRaw(peer, MessageCodec.Encode(message));
        }

        private void SendRaw(Peer peer, byte[] bytes)
        {
            peer.LastSentMs = _nowMs;
            BytesOut += bytes.Length;
            _transport.Send(peer.ConnectionId, bytes);
        }

        private string UniqueName(string name)
        {
            string candidate = name;
            int suffix = 2;
            while (NameTaken(candidate))
            {
                candidate = name + " (" + suffix + ")";
                suffix++;
            }

            return candidate;
        }

        private bool NameTaken(string name)
        {
            if (string.Equals(name, Config.ServerName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (PlayerInfo player in _players.Values)
            {
                if (string.Equals(player.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
