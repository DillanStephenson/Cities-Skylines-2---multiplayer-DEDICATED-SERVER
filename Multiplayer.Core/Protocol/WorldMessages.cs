using System.IO;
using Multiplayer.Core.Session;
using Multiplayer.Core.Wire;

namespace Multiplayer.Core.Protocol
{
    /// <summary>Server -> client after the handshake and whenever the stored world changes.</summary>
    public sealed class WorldInfoMessage : NetMessage
    {
        public bool HasWorld;
        public WorldInfo Info = new WorldInfo();

        public override MessageType Type => MessageType.WorldInfo;

        public override void Write(BinaryWriter writer)
        {
            writer.Write(HasWorld);
            if (HasWorld)
            {
                Info.Write(writer);
            }
        }

        public override void Read(BinaryReader reader)
        {
            HasWorld = reader.ReadBoolean();
            Info = new WorldInfo();
            if (HasWorld)
            {
                Info.Read(reader);
            }
        }
    }

    /// <summary>Client -> server: stream me revision N (the one WorldInfo announced).</summary>
    public sealed class WorldRequestMessage : NetMessage
    {
        public int Revision;

        public override MessageType Type => MessageType.WorldRequest;

        public override void Write(BinaryWriter writer)
        {
            writer.Write(Revision);
        }

        public override void Read(BinaryReader reader)
        {
            Revision = reader.ReadInt32();
        }
    }

    /// <summary>One slice of the world. Chunks arrive in order over the reliable transport; Offset guards against mix-ups.</summary>
    public sealed class WorldChunkMessage : NetMessage
    {
        public int Revision;
        public long Offset;
        public long TotalSize;
        public byte[] Data = new byte[0];

        public override MessageType Type => MessageType.WorldChunk;

        public override void Write(BinaryWriter writer)
        {
            writer.Write(Revision);
            writer.Write(Offset);
            writer.Write(TotalSize);
            writer.WriteBlob(Data);
        }

        public override void Read(BinaryReader reader)
        {
            Revision = reader.ReadInt32();
            Offset = reader.ReadInt64();
            TotalSize = reader.ReadInt64();
            Data = reader.ReadBlob(ProtocolConstants.WorldChunkBytes * 2) ?? new byte[0];
        }
    }

    /// <summary>Owner -> server: here comes a world of Size bytes with this hash; chunks follow, then WorldUploadEnd.</summary>
    public sealed class WorldUploadBeginMessage : NetMessage
    {
        public long Size;
        public string Sha256 = string.Empty;
        public string Guid = string.Empty;
        public string SaveName = string.Empty;
        public string CityName = string.Empty;

        public override MessageType Type => MessageType.WorldUploadBegin;

        public override void Write(BinaryWriter writer)
        {
            writer.Write(Size);
            writer.WriteText(Sha256);
            writer.WriteText(Guid);
            writer.WriteText(SaveName);
            writer.WriteText(CityName);
        }

        public override void Read(BinaryReader reader)
        {
            Size = reader.ReadInt64();
            Sha256 = reader.ReadText();
            Guid = reader.ReadText();
            SaveName = reader.ReadText();
            CityName = reader.ReadText();
        }
    }

    public sealed class WorldUploadEndMessage : NetMessage
    {
        public override MessageType Type => MessageType.WorldUploadEnd;

        public override void Write(BinaryWriter writer)
        {
        }

        public override void Read(BinaryReader reader)
        {
        }
    }

    /// <summary>Server -> owner: accepted (with the new revision) or why not.</summary>
    public sealed class WorldUploadResultMessage : NetMessage
    {
        public bool Accepted;
        public int Revision;
        public string Reason = string.Empty;

        public override MessageType Type => MessageType.WorldUploadResult;

        public override void Write(BinaryWriter writer)
        {
            writer.Write(Accepted);
            writer.Write(Revision);
            writer.WriteText(Reason);
        }

        public override void Read(BinaryReader reader)
        {
            Accepted = reader.ReadBoolean();
            Revision = reader.ReadInt32();
            Reason = reader.ReadText();
        }
    }
}
