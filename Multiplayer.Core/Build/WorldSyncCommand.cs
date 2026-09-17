using System;
using System.IO;
using Multiplayer.Core.Protocol;

namespace Multiplayer.Core.Build
{
    public enum WorldSyncPhase : byte
    {
        /// <summary>The leader is about to save: everyone stops and shows the sync box.</summary>
        Start = 1,

        /// <summary>The leader's save is on the server as this revision: everyone else loads it.</summary>
        Ready = 2,

        /// <summary>The save did not happen; carry on.</summary>
        Cancel = 3,
    }

    /// <summary>
    /// A forced sync of the whole group, driven by the leader: a start notice so everyone pauses behind a
    /// "Syncing world" box, then the revision to load once the leader's save is on the server.
    /// </summary>
    public sealed class WorldSyncCommand
    {
        public const string Kind = "worldsync";
        public const byte Version = 1;

        public WorldSyncPhase Phase = WorldSyncPhase.Start;
        public int Revision;

        public byte[] ToBytes()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Version);
                writer.Write((byte)Phase);
                writer.Write(Revision);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static WorldSyncCommand FromBytes(byte[] data)
        {
            try
            {
                using (var stream = new MemoryStream(data ?? new byte[0], false))
                using (var reader = new BinaryReader(stream))
                {
                    byte version = reader.ReadByte();
                    if (version != Version)
                    {
                        throw new ProtocolException("Unsupported world sync version " + version);
                    }

                    var command = new WorldSyncCommand { Phase = (WorldSyncPhase)reader.ReadByte(), Revision = reader.ReadInt32() };
                    if (command.Phase < WorldSyncPhase.Start || command.Phase > WorldSyncPhase.Cancel)
                    {
                        throw new ProtocolException("Unknown world sync phase " + (byte)command.Phase);
                    }

                    return command;
                }
            }
            catch (ProtocolException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ProtocolException("Malformed world sync: " + ex.Message, ex);
            }
        }

        public override string ToString()
        {
            return "world sync " + Phase + (Phase == WorldSyncPhase.Ready ? " (revision " + Revision + ")" : "");
        }
    }
}
