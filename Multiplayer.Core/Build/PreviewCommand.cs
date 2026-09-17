using System;
using System.Collections.Generic;
using System.IO;
using Multiplayer.Core.Protocol;

namespace Multiplayer.Core.Build
{
    /// <summary>A road piece being previewed: a cubic bezier on the map plus its width.</summary>
    public sealed class PreviewCurve
    {
        public Vec3 A;
        public Vec3 B;
        public Vec3 C;
        public Vec3 D;
        public float Width;
        public bool Deleting;
    }

    /// <summary>A building or prop being previewed: where and roughly how big.</summary>
    public sealed class PreviewPoint
    {
        public Vec3 Position;
        public float Radius;
        public bool Deleting;
    }

    /// <summary>An area (district, lot) being previewed: its outline.</summary>
    public sealed class PreviewLoop
    {
        public List<Vec3> Nodes = new List<Vec3>();
    }

    /// <summary>
    /// What a player's tool is showing but has not placed yet: the ghost of the road they are dragging out, the
    /// building under their cursor, the outline of an area. Geometry only, sent a few times a second while it
    /// changes; the others draw it as an overlay in that player's colour. An empty one clears it.
    /// </summary>
    public sealed class PreviewCommand
    {
        public const string Kind = "preview";
        public const byte Version = 1;
        public const int MaxItems = 2000;

        public List<PreviewCurve> Curves = new List<PreviewCurve>();
        public List<PreviewPoint> Points = new List<PreviewPoint>();
        public List<PreviewLoop> Loops = new List<PreviewLoop>();

        public bool IsEmpty => Curves.Count == 0 && Points.Count == 0 && Loops.Count == 0;

        public byte[] ToBytes()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Version);
                writer.Write(Curves.Count);
                foreach (PreviewCurve curve in Curves)
                {
                    curve.A.Write(writer);
                    curve.B.Write(writer);
                    curve.C.Write(writer);
                    curve.D.Write(writer);
                    writer.Write(curve.Width);
                    writer.Write(curve.Deleting);
                }

                writer.Write(Points.Count);
                foreach (PreviewPoint point in Points)
                {
                    point.Position.Write(writer);
                    writer.Write(point.Radius);
                    writer.Write(point.Deleting);
                }

                writer.Write(Loops.Count);
                foreach (PreviewLoop loop in Loops)
                {
                    writer.Write(loop.Nodes.Count);
                    foreach (Vec3 node in loop.Nodes)
                    {
                        node.Write(writer);
                    }
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        public static PreviewCommand FromBytes(byte[] data)
        {
            try
            {
                using (var stream = new MemoryStream(data ?? new byte[0], false))
                using (var reader = new BinaryReader(stream))
                {
                    byte version = reader.ReadByte();
                    if (version != Version)
                    {
                        throw new ProtocolException("Unsupported preview version " + version);
                    }

                    var command = new PreviewCommand();
                    int curves = Count(reader.ReadInt32());
                    for (int i = 0; i < curves; i++)
                    {
                        command.Curves.Add(new PreviewCurve { A = Vec3.Read(reader), B = Vec3.Read(reader), C = Vec3.Read(reader), D = Vec3.Read(reader), Width = reader.ReadSingle(), Deleting = reader.ReadBoolean() });
                    }

                    int points = Count(reader.ReadInt32());
                    for (int i = 0; i < points; i++)
                    {
                        command.Points.Add(new PreviewPoint { Position = Vec3.Read(reader), Radius = reader.ReadSingle(), Deleting = reader.ReadBoolean() });
                    }

                    int loops = Count(reader.ReadInt32());
                    for (int i = 0; i < loops; i++)
                    {
                        var loop = new PreviewLoop();
                        int nodes = Count(reader.ReadInt32());
                        for (int n = 0; n < nodes; n++)
                        {
                            loop.Nodes.Add(Vec3.Read(reader));
                        }

                        command.Loops.Add(loop);
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
                throw new ProtocolException("Malformed preview: " + ex.Message, ex);
            }
        }

        private static int Count(int value)
        {
            if (value < 0 || value > MaxItems)
            {
                throw new ProtocolException("Unreasonable preview item count " + value);
            }

            return value;
        }

        public override string ToString()
        {
            return IsEmpty ? "preview cleared" : "preview (" + Curves.Count + " curves, " + Points.Count + " points, " + Loops.Count + " areas)";
        }
    }
}
