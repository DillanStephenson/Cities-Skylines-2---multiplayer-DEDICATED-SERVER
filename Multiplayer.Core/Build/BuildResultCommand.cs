using System;
using System.IO;
using Multiplayer.Core.Protocol;
using Multiplayer.Core.Wire;

namespace Multiplayer.Core.Build
{
    /// <summary>
    /// What happened when a game replayed someone else's build command. Sent back so the builder learns when
    /// their road or building could not be recreated on another PC, instead of only that PC's log knowing.
    /// </summary>
    public sealed class BuildResultCommand
    {
        public const string Kind = "buildresult";
        public const byte Version = 1;
        public const int MaxMessageLength = 300;

        public int Sequence;
        public int BuilderPlayerId;
        public string ToolId = string.Empty;
        public bool Ok;
        public string Message = string.Empty;

        public byte[] ToBytes()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Version);
                writer.Write(Sequence);
                writer.Write(BuilderPlayerId);
                writer.WriteText(ToolId ?? string.Empty);
                writer.Write(Ok);
                string message = Message ?? string.Empty;
                writer.WriteText(message.Length > MaxMessageLength ? message.Substring(0, MaxMessageLength) : message);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static BuildResultCommand FromBytes(byte[] data)
        {
            try
            {
                using (var stream = new MemoryStream(data ?? new byte[0], false))
                using (var reader = new BinaryReader(stream))
                {
                    byte version = reader.ReadByte();
                    if (version != Version)
                    {
                        throw new ProtocolException("Unsupported build result version " + version);
                    }

                    return new BuildResultCommand
                    {
                        Sequence = reader.ReadInt32(),
                        BuilderPlayerId = reader.ReadInt32(),
                        ToolId = reader.ReadText(),
                        Ok = reader.ReadBoolean(),
                        Message = reader.ReadText(),
                    };
                }
            }
            catch (ProtocolException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ProtocolException("Malformed build result: " + ex.Message, ex);
            }
        }

        public override string ToString()
        {
            return "build #" + Sequence + " (" + ToolId + ") of player " + BuilderPlayerId + (Ok ? " ok" : " failed") + (string.IsNullOrEmpty(Message) ? "" : ": " + Message);
        }
    }
}
