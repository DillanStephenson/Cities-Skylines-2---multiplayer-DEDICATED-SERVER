using System;
using System.Collections.Generic;
using System.IO;
using Multiplayer.Core.Protocol;
using Multiplayer.Core.Wire;

namespace Multiplayer.Core.Build
{
    /// <summary>
    /// A player action as the game itself describes it: the "definition" entities a tool emits when the
    /// player clicks (build this road along this curve, place this building here, delete that thing).
    /// The receiving game recreates the definitions and lets its own generators and apply systems do the
    /// rest, so topology, costs and validation come from the game, not from us. Entities are referenced by
    /// prefab plus position because entity ids differ between game instances.
    /// </summary>
    public sealed class BuildCommand
    {
        public const string Kind = "build";
        public const byte Version = 1;

        /// <summary>Per-sender counter, for logs and duplicate detection.</summary>
        public int Sequence;

        /// <summary>The game's tool id that produced this (for logs only).</summary>
        public string ToolId = string.Empty;

        public List<DefinitionData> Definitions = new List<DefinitionData>();

        public byte[] ToBytes()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Version);
                writer.Write(Sequence);
                writer.WriteText(ToolId);
                writer.Write(Definitions.Count);
                foreach (DefinitionData definition in Definitions)
                {
                    definition.Write(writer);
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        public static BuildCommand FromBytes(byte[] data)
        {
            try
            {
                using (var stream = new MemoryStream(data ?? new byte[0], false))
                using (var reader = new BinaryReader(stream))
                {
                    byte version = reader.ReadByte();
                    if (version != Version)
                    {
                        throw new ProtocolException("Unsupported build command version " + version);
                    }

                    var command = new BuildCommand { Sequence = reader.ReadInt32(), ToolId = reader.ReadText() };
                    int count = reader.ReadInt32();
                    if (count < 0 || count > 100000)
                    {
                        throw new ProtocolException("Unreasonable definition count " + count);
                    }

                    for (int i = 0; i < count; i++)
                    {
                        command.Definitions.Add(DefinitionData.Read(reader));
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
                throw new ProtocolException("Malformed build command: " + ex.Message, ex);
            }
        }

        public override string ToString()
        {
            return "build #" + Sequence + " (" + ToolId + ", " + Definitions.Count + " definitions)";
        }
    }

    public struct Vec3
    {
        public float X, Y, Z;

        public Vec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public void Write(BinaryWriter w)
        {
            w.Write(X);
            w.Write(Y);
            w.Write(Z);
        }

        public static Vec3 Read(BinaryReader r)
        {
            return new Vec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        }

        public override string ToString()
        {
            return "(" + X.ToString("0.#") + ", " + Y.ToString("0.#") + ", " + Z.ToString("0.#") + ")";
        }
    }

    public struct Quat
    {
        public float X, Y, Z, W;

        public Quat(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public static Quat Identity => new Quat(0, 0, 0, 1);

        public void Write(BinaryWriter w)
        {
            w.Write(X);
            w.Write(Y);
            w.Write(Z);
            w.Write(W);
        }

        public static Quat Read(BinaryReader r)
        {
            return new Quat(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        }
    }

    /// <summary>Identifies a prefab across game instances.</summary>
    public sealed class PrefabKey
    {
        public string Type = string.Empty;
        public string Name = string.Empty;

        public bool IsEmpty => string.IsNullOrEmpty(Name);

        public void Write(BinaryWriter w)
        {
            w.WriteText(Type);
            w.WriteText(Name);
        }

        public static PrefabKey Read(BinaryReader r)
        {
            return new PrefabKey { Type = r.ReadText(), Name = r.ReadText() };
        }

        public override string ToString()
        {
            return IsEmpty ? "-" : Name;
        }
    }

    public enum EntityKind : byte
    {
        None = 0,
        NetNode = 1,
        NetEdge = 2,
        Object = 3,
        Area = 4,

        /// <summary>A zoning block alongside a road (what the zone tool hits).</summary>
        ZoneBlock = 5,

        /// <summary>A transport line: prefab plus line number (in Aux.X). Lines have no position.</summary>
        Route = 6,

        /// <summary>A waypoint or stop of a transport line, by position.</summary>
        Waypoint = 7,

        /// <summary>A lane inside a road or junction (what per-lane mod settings hang off), by curve midpoint and start.</summary>
        Lane = 8,

        /// <summary>An entity another mod created on its own (a depot zone, a settings singleton): the mod's identity
        /// component type name in Prefab.Name, its position field in Position when it has one.</summary>
        ModEntity = 9,

        Unresolvable = 255,
    }

    /// <summary>A reference to an existing entity by what it is and where it is.</summary>
    public sealed class EntityRef
    {
        public EntityKind Kind = EntityKind.None;
        public PrefabKey Prefab = new PrefabKey();
        public Vec3 Position;

        /// <summary>Edges: position of the start node, to disambiguate two edges sharing a midpoint.</summary>
        public Vec3 Aux;

        public static readonly EntityRef None = new EntityRef();

        public void Write(BinaryWriter w)
        {
            w.Write((byte)Kind);
            if (Kind == EntityKind.None)
            {
                return;
            }

            Prefab.Write(w);
            Position.Write(w);
            Aux.Write(w);
        }

        public static EntityRef Read(BinaryReader r)
        {
            var reference = new EntityRef { Kind = (EntityKind)r.ReadByte() };
            if (reference.Kind == EntityKind.None)
            {
                return reference;
            }

            reference.Prefab = PrefabKey.Read(r);
            reference.Position = Vec3.Read(r);
            reference.Aux = Vec3.Read(r);
            return reference;
        }

        public override string ToString()
        {
            return Kind == EntityKind.None ? "-" : Kind == EntityKind.Route ? Kind + ":" + Prefab + " #" + (int)Aux.X : Kind + ":" + Prefab + "@" + Position;
        }
    }

    public sealed class CoursePosData
    {
        public EntityRef Entity = new EntityRef();
        public Vec3 Position;
        public Quat Rotation = Quat.Identity;
        public float ElevationX, ElevationY;
        public float CourseDelta;
        public float SplitPosition;
        public uint Flags;
        public int ParentMesh;

        public void Write(BinaryWriter w)
        {
            Entity.Write(w);
            Position.Write(w);
            Rotation.Write(w);
            w.Write(ElevationX);
            w.Write(ElevationY);
            w.Write(CourseDelta);
            w.Write(SplitPosition);
            w.Write(Flags);
            w.Write(ParentMesh);
        }

        public static CoursePosData Read(BinaryReader r)
        {
            return new CoursePosData
            {
                Entity = EntityRef.Read(r),
                Position = Vec3.Read(r),
                Rotation = Quat.Read(r),
                ElevationX = r.ReadSingle(),
                ElevationY = r.ReadSingle(),
                CourseDelta = r.ReadSingle(),
                SplitPosition = r.ReadSingle(),
                Flags = r.ReadUInt32(),
                ParentMesh = r.ReadInt32(),
            };
        }
    }

    public sealed class NetCourseData
    {
        public CoursePosData Start = new CoursePosData();
        public CoursePosData End = new CoursePosData();
        public Vec3 A, B, C, D;
        public float ElevationX, ElevationY;
        public float Length;
        public int FixedIndex;

        public void Write(BinaryWriter w)
        {
            Start.Write(w);
            End.Write(w);
            A.Write(w);
            B.Write(w);
            C.Write(w);
            D.Write(w);
            w.Write(ElevationX);
            w.Write(ElevationY);
            w.Write(Length);
            w.Write(FixedIndex);
        }

        public static NetCourseData Read(BinaryReader r)
        {
            return new NetCourseData
            {
                Start = CoursePosData.Read(r),
                End = CoursePosData.Read(r),
                A = Vec3.Read(r),
                B = Vec3.Read(r),
                C = Vec3.Read(r),
                D = Vec3.Read(r),
                ElevationX = r.ReadSingle(),
                ElevationY = r.ReadSingle(),
                Length = r.ReadSingle(),
                FixedIndex = r.ReadInt32(),
            };
        }
    }

    public sealed class ObjectDefinitionData
    {
        public Vec3 Position, LocalPosition, Scale;
        public Quat Rotation = Quat.Identity, LocalRotation = Quat.Identity;
        public float Elevation, Intensity, Age;
        public bool IsDecoration;
        public int ParentMesh, GroupIndex, Probability, PrefabSubIndex;

        public void Write(BinaryWriter w)
        {
            Position.Write(w);
            LocalPosition.Write(w);
            Scale.Write(w);
            Rotation.Write(w);
            LocalRotation.Write(w);
            w.Write(Elevation);
            w.Write(Intensity);
            w.Write(Age);
            w.Write(IsDecoration);
            w.Write(ParentMesh);
            w.Write(GroupIndex);
            w.Write(Probability);
            w.Write(PrefabSubIndex);
        }

        public static ObjectDefinitionData Read(BinaryReader r)
        {
            return new ObjectDefinitionData
            {
                Position = Vec3.Read(r),
                LocalPosition = Vec3.Read(r),
                Scale = Vec3.Read(r),
                Rotation = Quat.Read(r),
                LocalRotation = Quat.Read(r),
                Elevation = r.ReadSingle(),
                Intensity = r.ReadSingle(),
                Age = r.ReadSingle(),
                IsDecoration = r.ReadBoolean(),
                ParentMesh = r.ReadInt32(),
                GroupIndex = r.ReadInt32(),
                Probability = r.ReadInt32(),
                PrefabSubIndex = r.ReadInt32(),
            };
        }
    }

    public sealed class OwnerDefinitionData
    {
        public PrefabKey Prefab = new PrefabKey();
        public Vec3 Position;
        public Quat Rotation = Quat.Identity;

        public void Write(BinaryWriter w)
        {
            Prefab.Write(w);
            Position.Write(w);
            Rotation.Write(w);
        }

        public static OwnerDefinitionData Read(BinaryReader r)
        {
            return new OwnerDefinitionData { Prefab = PrefabKey.Read(r), Position = Vec3.Read(r), Rotation = Quat.Read(r) };
        }
    }

    public struct AreaNodeData
    {
        public Vec3 Position;
        public float Elevation;
    }

    /// <summary>Zone tool: the cell quad being painted and how (fill, marquee, clear ...).</summary>
    public sealed class ZoningData
    {
        public Vec3 A, B, C, D;
        public uint Flags;

        public void Write(BinaryWriter w)
        {
            A.Write(w);
            B.Write(w);
            C.Write(w);
            D.Write(w);
            w.Write(Flags);
        }

        public static ZoningData Read(BinaryReader r)
        {
            return new ZoningData { A = Vec3.Read(r), B = Vec3.Read(r), C = Vec3.Read(r), D = Vec3.Read(r), Flags = r.ReadUInt32() };
        }
    }

    /// <summary>
    /// A terraforming stroke. The definition prefab is the brush shape; the tool prefab says what the stroke
    /// does (raise, level, soften, slope, or paint a resource or surface). One arrives per frame while the
    /// mouse is held, which is how the game itself works.
    /// </summary>
    public sealed class BrushDefinitionData
    {
        public PrefabKey Tool = new PrefabKey();
        public Vec3 LineA, LineB, Target, Start;
        public float Angle, Size, Strength, Time;

        public void Write(BinaryWriter w)
        {
            Tool.Write(w);
            LineA.Write(w);
            LineB.Write(w);
            Target.Write(w);
            Start.Write(w);
            w.Write(Angle);
            w.Write(Size);
            w.Write(Strength);
            w.Write(Time);
        }

        public static BrushDefinitionData Read(BinaryReader r)
        {
            return new BrushDefinitionData
            {
                Tool = PrefabKey.Read(r),
                LineA = Vec3.Read(r),
                LineB = Vec3.Read(r),
                Target = Vec3.Read(r),
                Start = Vec3.Read(r),
                Angle = r.ReadSingle(),
                Size = r.ReadSingle(),
                Strength = r.ReadSingle(),
                Time = r.ReadSingle(),
            };
        }
    }

    /// <summary>One waypoint of a transport line being created or edited: a position, optionally the stop it sits on and the existing waypoint it replaces.</summary>
    public sealed class WaypointData
    {
        public Vec3 Position;
        public EntityRef Connection = new EntityRef();
        public EntityRef Original = new EntityRef();

        public void Write(BinaryWriter w)
        {
            Position.Write(w);
            Connection.Write(w);
            Original.Write(w);
        }

        public static WaypointData Read(BinaryReader r)
        {
            return new WaypointData { Position = Vec3.Read(r), Connection = EntityRef.Read(r), Original = EntityRef.Read(r) };
        }
    }

    /// <summary>The colour picked for a transport line.</summary>
    public sealed class ColorData
    {
        public byte R, G, B, A;

        public void Write(BinaryWriter w)
        {
            w.Write(R);
            w.Write(G);
            w.Write(B);
            w.Write(A);
        }

        public static ColorData Read(BinaryReader r)
        {
            return new ColorData { R = r.ReadByte(), G = r.ReadByte(), B = r.ReadByte(), A = r.ReadByte() };
        }
    }

    /// <summary>One definition entity. Optional parts are present or absent exactly as on the sending game.</summary>
    public sealed class DefinitionData
    {
        public PrefabKey Prefab = new PrefabKey();
        public PrefabKey SubPrefab = new PrefabKey();
        public EntityRef Original = new EntityRef();
        public EntityRef Owner = new EntityRef();
        public EntityRef Attached = new EntityRef();
        public uint Flags;
        public int RandomSeed;

        public NetCourseData Course;
        public ObjectDefinitionData Object;
        public OwnerDefinitionData OwnerDefinition;
        public List<AreaNodeData> AreaNodes;
        public ZoningData Zoning;
        public BrushDefinitionData Brush;
        public List<WaypointData> Waypoints;
        public ColorData Color;

        private const byte HasCourse = 1;
        private const byte HasObject = 2;
        private const byte HasOwnerDefinition = 4;
        private const byte HasAreaNodes = 8;
        private const byte HasZoning = 16;
        private const byte HasBrush = 32;
        private const byte HasWaypoints = 64;
        private const byte HasColor = 128;

        public void Write(BinaryWriter w)
        {
            Prefab.Write(w);
            SubPrefab.Write(w);
            Original.Write(w);
            Owner.Write(w);
            Attached.Write(w);
            w.Write(Flags);
            w.Write(RandomSeed);

            byte parts = 0;
            if (Course != null) parts |= HasCourse;
            if (Object != null) parts |= HasObject;
            if (OwnerDefinition != null) parts |= HasOwnerDefinition;
            if (AreaNodes != null) parts |= HasAreaNodes;
            if (Zoning != null) parts |= HasZoning;
            if (Brush != null) parts |= HasBrush;
            if (Waypoints != null) parts |= HasWaypoints;
            if (Color != null) parts |= HasColor;
            w.Write(parts);

            Course?.Write(w);
            Object?.Write(w);
            OwnerDefinition?.Write(w);
            if (AreaNodes != null)
            {
                w.Write(AreaNodes.Count);
                foreach (AreaNodeData node in AreaNodes)
                {
                    node.Position.Write(w);
                    w.Write(node.Elevation);
                }
            }

            Zoning?.Write(w);
            Brush?.Write(w);
            if (Waypoints != null)
            {
                w.Write(Waypoints.Count);
                foreach (WaypointData waypoint in Waypoints)
                {
                    waypoint.Write(w);
                }
            }

            Color?.Write(w);
        }

        public static DefinitionData Read(BinaryReader r)
        {
            var definition = new DefinitionData
            {
                Prefab = PrefabKey.Read(r),
                SubPrefab = PrefabKey.Read(r),
                Original = EntityRef.Read(r),
                Owner = EntityRef.Read(r),
                Attached = EntityRef.Read(r),
                Flags = r.ReadUInt32(),
                RandomSeed = r.ReadInt32(),
            };

            byte parts = r.ReadByte();
            if ((parts & HasCourse) != 0)
            {
                definition.Course = NetCourseData.Read(r);
            }

            if ((parts & HasObject) != 0)
            {
                definition.Object = ObjectDefinitionData.Read(r);
            }

            if ((parts & HasOwnerDefinition) != 0)
            {
                definition.OwnerDefinition = OwnerDefinitionData.Read(r);
            }

            if ((parts & HasAreaNodes) != 0)
            {
                int count = r.ReadInt32();
                if (count < 0 || count > 100000)
                {
                    throw new ProtocolException("Unreasonable area node count " + count);
                }

                definition.AreaNodes = new List<AreaNodeData>(count);
                for (int i = 0; i < count; i++)
                {
                    definition.AreaNodes.Add(new AreaNodeData { Position = Vec3.Read(r), Elevation = r.ReadSingle() });
                }
            }

            if ((parts & HasZoning) != 0)
            {
                definition.Zoning = ZoningData.Read(r);
            }

            if ((parts & HasBrush) != 0)
            {
                definition.Brush = BrushDefinitionData.Read(r);
            }

            if ((parts & HasWaypoints) != 0)
            {
                int count = r.ReadInt32();
                if (count < 0 || count > 10000)
                {
                    throw new ProtocolException("Unreasonable waypoint count " + count);
                }

                definition.Waypoints = new List<WaypointData>(count);
                for (int i = 0; i < count; i++)
                {
                    definition.Waypoints.Add(WaypointData.Read(r));
                }
            }

            if ((parts & HasColor) != 0)
            {
                definition.Color = ColorData.Read(r);
            }

            return definition;
        }

        public override string ToString()
        {
            string what = Course != null ? "net" : Object != null ? "object" : AreaNodes != null ? "area" : Zoning != null ? "zoning" : Brush != null ? "brush" : Waypoints != null ? "line" : "other";
            return what + " " + Prefab + " flags=0x" + Flags.ToString("x") + (Original.Kind != EntityKind.None ? " original=" + Original : "");
        }
    }
}
