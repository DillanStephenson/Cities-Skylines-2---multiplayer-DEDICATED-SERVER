using System.Collections.Generic;
using System.IO;
using Multiplayer.Core.Wire;

namespace Multiplayer.Core.Protocol
{
    /// <summary>Client -> server, first thing sent after the socket opens.</summary>
    public sealed class HandshakeRequestMessage : NetMessage
    {
        public int ProtocolVersion = ProtocolConstants.ProtocolVersion;
        public string ModVersion = ProtocolConstants.ModVersion;
        public string GameVersion = string.Empty;
        public string PlayerName = string.Empty;
        public string Password = string.Empty;

        /// <summary>Secret that makes this client the session owner (the hosting player). Empty for ordinary players.</summary>
        public string OwnerKey = string.Empty;

        /// <summary>Names of every enabled mod, as the game reports them. The server compares players against the host.</summary>
        public List<string> Mods = new List<string>();

        /// <summary>Name of the active Paradox playset, so rejected joiners can be told which one to activate.</summary>
        public string Playset = string.Empty;

        public override MessageType Type => MessageType.HandshakeRequest;

        public override void Write(BinaryWriter writer)
        {
            writer.Write(ProtocolVersion);
            writer.WriteText(ModVersion);
            writer.WriteText(GameVersion);
            writer.WriteText(PlayerName);
            writer.WriteText(Password);
            writer.WriteText(OwnerKey);
            writer.Write(Mods.Count);
            foreach (string mod in Mods)
            {
                writer.WriteText(mod);
            }

            writer.WriteText(Playset ?? string.Empty);
        }

        public override void Read(BinaryReader reader)
        {
            ProtocolVersion = reader.ReadInt32();
            ModVersion = reader.ReadText();
            GameVersion = reader.ReadText();
            PlayerName = reader.ReadText();
            Password = reader.ReadText();
            OwnerKey = reader.ReadText();
            int count = reader.ReadInt32();
            if (count < 0 || count > ProtocolConstants.MaxModListEntries)
            {
                throw new ProtocolException("Unreasonable mod count " + count);
            }

            Mods = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                Mods.Add(reader.ReadText());
            }

            Playset = reader.ReadText();
        }
    }

    /// <summary>Server -> client answer to the handshake. A rejected client is disconnected right after.</summary>
    public sealed class HandshakeResponseMessage : NetMessage
    {
        public bool Accepted;
        public string Reason = string.Empty;
        public int PlayerId;
        public bool IsOwner;
        public string ServerName = string.Empty;

        public override MessageType Type => MessageType.HandshakeResponse;

        public override void Write(BinaryWriter writer)
        {
            writer.Write(Accepted);
            writer.WriteText(Reason);
            writer.Write(PlayerId);
            writer.Write(IsOwner);
            writer.WriteText(ServerName);
        }

        public override void Read(BinaryReader reader)
        {
            Accepted = reader.ReadBoolean();
            Reason = reader.ReadText();
            PlayerId = reader.ReadInt32();
            IsOwner = reader.ReadBoolean();
            ServerName = reader.ReadText();
        }
    }

    /// <summary>Either direction: polite notice before the socket closes.</summary>
    public sealed class DisconnectMessage : NetMessage
    {
        public string Reason = string.Empty;

        public override MessageType Type => MessageType.Disconnect;

        public override void Write(BinaryWriter writer)
        {
            writer.WriteText(Reason);
        }

        public override void Read(BinaryReader reader)
        {
            Reason = reader.ReadText();
        }
    }

    /// <summary>Keeps idle connections alive and lets both sides detect a dead peer.</summary>
    public sealed class HeartbeatMessage : NetMessage
    {
        public override MessageType Type => MessageType.Heartbeat;

        public override void Write(BinaryWriter writer)
        {
        }

        public override void Read(BinaryReader reader)
        {
        }
    }

    /// <summary>PlayerId 0 is the server itself talking.</summary>
    public sealed class ChatMessage : NetMessage
    {
        public int PlayerId;
        public string PlayerName = string.Empty;
        public string Text = string.Empty;

        public override MessageType Type => MessageType.Chat;

        public override void Write(BinaryWriter writer)
        {
            writer.Write(PlayerId);
            writer.WriteText(PlayerName);
            writer.WriteText(Text);
        }

        public override void Read(BinaryReader reader)
        {
            PlayerId = reader.ReadInt32();
            PlayerName = reader.ReadText();
            Text = reader.ReadText();
        }
    }

    public struct PlayerListEntry
    {
        public int PlayerId;
        public string Name;
        public bool IsOwner;
    }

    /// <summary>Server -> clients whenever the roster changes. Always the full list, never a delta.</summary>
    public sealed class PlayerListMessage : NetMessage
    {
        public List<PlayerListEntry> Players = new List<PlayerListEntry>();

        public override MessageType Type => MessageType.PlayerList;

        public override void Write(BinaryWriter writer)
        {
            writer.Write(Players.Count);
            foreach (PlayerListEntry entry in Players)
            {
                writer.Write(entry.PlayerId);
                writer.WriteText(entry.Name);
                writer.Write(entry.IsOwner);
            }
        }

        public override void Read(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > ProtocolConstants.MaxPlayers * 4)
            {
                throw new ProtocolException($"Unreasonable player count {count}");
            }

            Players = new List<PlayerListEntry>(count);
            for (int i = 0; i < count; i++)
            {
                Players.Add(new PlayerListEntry
                {
                    PlayerId = reader.ReadInt32(),
                    Name = reader.ReadText(),
                    IsOwner = reader.ReadBoolean(),
                });
            }
        }
    }

    /// <summary>
    /// Client -> server: "please run at this speed". Server -> clients: "we are running at this speed".
    /// 0 means paused, matching Game.Simulation.SimulationSystem.selectedSpeed.
    /// </summary>
    public sealed class SimulationSpeedMessage : NetMessage
    {
        public float Speed;

        public override MessageType Type => MessageType.SimulationSpeed;

        public override void Write(BinaryWriter writer)
        {
            writer.Write(Speed);
        }

        public override void Read(BinaryReader reader)
        {
            Speed = reader.ReadSingle();
        }
    }

    public enum ServerControlAction : byte
    {
        /// <summary>Shut the server down, telling everyone the given reason. Owner only.</summary>
        Stop = 1,
    }

    /// <summary>Owner -> server admin request. Anyone else sending it is ignored.</summary>
    public sealed class ServerControlMessage : NetMessage
    {
        public ServerControlAction Action = ServerControlAction.Stop;
        public string Text = string.Empty;

        public override MessageType Type => MessageType.ServerControl;

        public override void Write(BinaryWriter writer)
        {
            writer.Write((byte)Action);
            writer.WriteText(Text);
        }

        public override void Read(BinaryReader reader)
        {
            Action = (ServerControlAction)reader.ReadByte();
            Text = reader.ReadText();
        }
    }

    /// <summary>
    /// Envelope for gameplay actions (build a road, zone a block, ...). The server relays it without
    /// understanding the payload; game-side systems register a handler per <see cref="Kind"/>.
    /// </summary>
    public sealed class GameplayCommandMessage : NetMessage
    {
        public string Kind = string.Empty;
        public int OriginPlayerId;
        public byte[] Payload = new byte[0];

        public override MessageType Type => MessageType.GameplayCommand;

        public override void Write(BinaryWriter writer)
        {
            writer.WriteText(Kind);
            writer.Write(OriginPlayerId);
            writer.WriteBlob(Payload);
        }

        public override void Read(BinaryReader reader)
        {
            Kind = reader.ReadText();
            OriginPlayerId = reader.ReadInt32();
            Payload = reader.ReadBlob(ProtocolConstants.MaxFrameBytes) ?? new byte[0];
        }
    }
}
