using System.IO;

namespace Multiplayer.Core.Wire
{
    /// <summary>Small helpers so messages never write a null string or an unbounded blob.</summary>
    public static class BinaryExtensions
    {
        public static void WriteText(this BinaryWriter writer, string value)
        {
            writer.Write(value ?? string.Empty);
        }

        public static string ReadText(this BinaryReader reader)
        {
            return reader.ReadString();
        }

        /// <summary>Length-prefixed byte array; null is encoded as length -1.</summary>
        public static void WriteBlob(this BinaryWriter writer, byte[] value)
        {
            if (value == null)
            {
                writer.Write(-1);
                return;
            }

            writer.Write(value.Length);
            writer.Write(value);
        }

        public static byte[] ReadBlob(this BinaryReader reader, int maxLength)
        {
            int length = reader.ReadInt32();
            if (length < 0)
            {
                return null;
            }

            if (length > maxLength)
            {
                throw new Protocol.ProtocolException($"Blob of {length} bytes exceeds the limit of {maxLength}");
            }

            byte[] data = reader.ReadBytes(length);
            if (data.Length != length)
            {
                throw new Protocol.ProtocolException("Blob was truncated");
            }

            return data;
        }
    }
}
