using System;
using System.Collections.Generic;
using System.IO;
using Multiplayer.Core.Protocol;
using Multiplayer.Core.Wire;

namespace Multiplayer.Core.Build
{
    /// <summary>One custom lane connection inside a group: which edge's lane goes to which edge's lane.</summary>
    public sealed class LaneConnectionData
    {
        public EntityRef Source = new EntityRef();
        public EntityRef Target = new EntityRef();
        public int LaneIndexX;
        public int LaneIndexY;
        public int[] GroupMap = new int[4];
        public Vec3 PositionA;
        public Vec3 PositionB;
        public int Method;
        public bool IsUnsafe;

        public void Write(BinaryWriter w)
        {
            Source.Write(w);
            Target.Write(w);
            w.Write(LaneIndexX);
            w.Write(LaneIndexY);
            for (int i = 0; i < 4; i++)
            {
                w.Write(GroupMap[i]);
            }

            PositionA.Write(w);
            PositionB.Write(w);
            w.Write(Method);
            w.Write(IsUnsafe);
        }

        public static LaneConnectionData Read(BinaryReader r)
        {
            var data = new LaneConnectionData { Source = EntityRef.Read(r), Target = EntityRef.Read(r), LaneIndexX = r.ReadInt32(), LaneIndexY = r.ReadInt32() };
            for (int i = 0; i < 4; i++)
            {
                data.GroupMap[i] = r.ReadInt32();
            }

            data.PositionA = Vec3.Read(r);
            data.PositionB = Vec3.Read(r);
            data.Method = r.ReadInt32();
            data.IsUnsafe = r.ReadBoolean();
            return data;
        }
    }

    /// <summary>The Traffic mod's connection group for one lane of one edge at a junction.</summary>
    public sealed class LaneGroupData
    {
        public int LaneIndex;
        public int CarriagewayX;
        public int CarriagewayY;
        public Vec3 LanePosition;
        public EntityRef Edge = new EntityRef();
        public List<LaneConnectionData> Connections = new List<LaneConnectionData>();

        public void Write(BinaryWriter w)
        {
            w.Write(LaneIndex);
            w.Write(CarriagewayX);
            w.Write(CarriagewayY);
            LanePosition.Write(w);
            Edge.Write(w);
            w.Write(Connections.Count);
            foreach (LaneConnectionData connection in Connections)
            {
                connection.Write(w);
            }
        }

        public static LaneGroupData Read(BinaryReader r)
        {
            var group = new LaneGroupData { LaneIndex = r.ReadInt32(), CarriagewayX = r.ReadInt32(), CarriagewayY = r.ReadInt32(), LanePosition = Vec3.Read(r), Edge = EntityRef.Read(r) };
            int count = r.ReadInt32();
            if (count < 0 || count > 10000)
            {
                throw new ProtocolException("Unreasonable connection count " + count);
            }

            for (int i = 0; i < count; i++)
            {
                group.Connections.Add(LaneConnectionData.Read(r));
            }

            return group;
        }
    }

    /// <summary>
    /// The Traffic mod's custom lane connections of one junction, spelled out field by field: the junction keeps
    /// one group per modified lane, and each group points at a hidden data entity holding the generated
    /// connections. The receiving game recreates those entities, so every entity reference travels by prefab
    /// and position and nothing depends on the mod's struct layout.
    /// </summary>
    public sealed class LaneConnectionsCommand
    {
        public const string Kind = "trafficlanes";
        public const byte Version = 1;

        public EntityRef Node = new EntityRef();
        public bool Remove;
        public List<LaneGroupData> Groups = new List<LaneGroupData>();

        public byte[] ToBytes()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Version);
                Node.Write(writer);
                writer.Write(Remove);
                writer.Write(Groups.Count);
                foreach (LaneGroupData group in Groups)
                {
                    group.Write(writer);
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        public static LaneConnectionsCommand FromBytes(byte[] data)
        {
            try
            {
                using (var stream = new MemoryStream(data ?? new byte[0], false))
                using (var reader = new BinaryReader(stream))
                {
                    byte version = reader.ReadByte();
                    if (version != Version)
                    {
                        throw new ProtocolException("Unsupported lane connections version " + version);
                    }

                    var command = new LaneConnectionsCommand { Node = EntityRef.Read(reader), Remove = reader.ReadBoolean() };
                    int count = reader.ReadInt32();
                    if (count < 0 || count > 10000)
                    {
                        throw new ProtocolException("Unreasonable group count " + count);
                    }

                    for (int i = 0; i < count; i++)
                    {
                        command.Groups.Add(LaneGroupData.Read(reader));
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
                throw new ProtocolException("Malformed lane connections: " + ex.Message, ex);
            }
        }

        public int ConnectionCount
        {
            get
            {
                int total = 0;
                foreach (LaneGroupData group in Groups)
                {
                    total += group.Connections.Count;
                }

                return total;
            }
        }

        public override string ToString()
        {
            return (Remove ? "remove lane connections on " : "lane connections (" + Groups.Count + " lanes, " + ConnectionCount + " links) on ") + Node;
        }
    }
}
