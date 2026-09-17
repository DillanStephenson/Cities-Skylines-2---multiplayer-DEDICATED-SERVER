using System;
using System.IO;
using Multiplayer.Core.Protocol;

namespace Multiplayer.Core.Build
{
    /// <summary>
    /// Where a player is on the map: the point their camera looks at, which way it faces, how far it is
    /// zoomed out, and the spot under their mouse. Sent a few times a second while it changes; others draw
    /// a marker and a name tag from it.
    /// </summary>
    public sealed class PresenceCommand
    {
        public const string Kind = "presence";
        public const byte Version = 1;

        public Vec3 Pivot;
        public float Yaw;
        public float Zoom;
        public bool HasCursor;
        public Vec3 Cursor;

        public byte[] ToBytes()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Version);
                Pivot.Write(writer);
                writer.Write(Yaw);
                writer.Write(Zoom);
                writer.Write(HasCursor);
                Cursor.Write(writer);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static PresenceCommand FromBytes(byte[] data)
        {
            try
            {
                using (var stream = new MemoryStream(data ?? new byte[0], false))
                using (var reader = new BinaryReader(stream))
                {
                    byte version = reader.ReadByte();
                    if (version != Version)
                    {
                        throw new ProtocolException("Unsupported presence version " + version);
                    }

                    return new PresenceCommand
                    {
                        Pivot = Vec3.Read(reader),
                        Yaw = reader.ReadSingle(),
                        Zoom = reader.ReadSingle(),
                        HasCursor = reader.ReadBoolean(),
                        Cursor = Vec3.Read(reader),
                    };
                }
            }
            catch (ProtocolException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ProtocolException("Malformed presence: " + ex.Message, ex);
            }
        }

        /// <summary>True when the two are far enough apart to be worth sending.</summary>
        public bool DiffersFrom(PresenceCommand other, float distance)
        {
            if (other == null || HasCursor != other.HasCursor)
            {
                return true;
            }

            return Far(Pivot, other.Pivot, distance) || Math.Abs(Yaw - other.Yaw) > 2f || Math.Abs(Zoom - other.Zoom) > Math.Max(20f, other.Zoom * 0.1f)
                || (HasCursor && Far(Cursor, other.Cursor, distance));
        }

        private static bool Far(Vec3 a, Vec3 b, float distance)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz > distance * distance;
        }

        public override string ToString()
        {
            return "presence at " + Pivot + " yaw " + Yaw.ToString("0") + " zoom " + Zoom.ToString("0") + (HasCursor ? " cursor " + Cursor : "");
        }
    }
}
