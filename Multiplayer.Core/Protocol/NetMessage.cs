using System.IO;

namespace Multiplayer.Core.Protocol
{
    /// <summary>Base class for everything that travels between peers. Subclasses are plain data plus Read/Write.</summary>
    public abstract class NetMessage
    {
        public abstract MessageType Type { get; }

        public abstract void Write(BinaryWriter writer);

        public abstract void Read(BinaryReader reader);
    }
}
