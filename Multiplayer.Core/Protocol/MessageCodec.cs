using System;
using System.IO;

namespace Multiplayer.Core.Protocol
{
    /// <summary>Frame body layout: [ushort MessageType][message-specific fields].</summary>
    public static class MessageCodec
    {
        public static byte[] Encode(NetMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((ushort)message.Type);
                message.Write(writer);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static NetMessage Decode(byte[] data)
        {
            if (data == null || data.Length < 2)
            {
                throw new ProtocolException("Frame too short to hold a message type");
            }

            try
            {
                using (var stream = new MemoryStream(data, false))
                using (var reader = new BinaryReader(stream))
                {
                    var type = (MessageType)reader.ReadUInt16();
                    NetMessage message = Create(type);
                    message.Read(reader);
                    return message;
                }
            }
            catch (ProtocolException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ProtocolException("Malformed message: " + ex.Message, ex);
            }
        }

        private static NetMessage Create(MessageType type)
        {
            switch (type)
            {
                case MessageType.HandshakeRequest: return new HandshakeRequestMessage();
                case MessageType.HandshakeResponse: return new HandshakeResponseMessage();
                case MessageType.Disconnect: return new DisconnectMessage();
                case MessageType.Heartbeat: return new HeartbeatMessage();
                case MessageType.Chat: return new ChatMessage();
                case MessageType.PlayerList: return new PlayerListMessage();
                case MessageType.SimulationSpeed: return new SimulationSpeedMessage();
                case MessageType.GameplayCommand: return new GameplayCommandMessage();
                case MessageType.ServerControl: return new ServerControlMessage();
                case MessageType.WorldInfo: return new WorldInfoMessage();
                case MessageType.WorldRequest: return new WorldRequestMessage();
                case MessageType.WorldChunk: return new WorldChunkMessage();
                case MessageType.WorldUploadBegin: return new WorldUploadBeginMessage();
                case MessageType.WorldUploadEnd: return new WorldUploadEndMessage();
                case MessageType.WorldUploadResult: return new WorldUploadResultMessage();
                default:
                    throw new ProtocolException($"Unknown message type {(ushort)type}");
            }
        }
    }
}
