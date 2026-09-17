using System;
using System.Collections.Generic;
using System.Reflection;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Tools;
using Multiplayer.Core.Build;
using Multiplayer.Core.Session;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;

namespace Multiplayer.Sync
{
    /// <summary>A component or buffer another mod keeps on entities, and how to treat it.</summary>
    internal sealed class TrackedType
    {
        public string TypeName = string.Empty;
        public bool IsBuffer;

        /// <summary>Add Game.Common.Updated after applying, so the owning mod re-initialises the entity as it would after a click.</summary>
        public bool MarkUpdated = true;

        /// <summary>The component is the identity of a standalone entity the mod creates; it is created and destroyed with it.</summary>
        public bool OwnsEntity;

        /// <summary>float3 field that tells such standalone entities apart; null when there is only ever one.</summary>
        public string PositionField;

        /// <summary>An empty marker component: only its presence is mirrored.</summary>
        public bool IsTag;

        /// <summary>Extra runtime fields of this type (normalised names: lower case, no m_), on top of the common list.</summary>
        public string[] RuntimeFields;
    }

    /// <summary>Where a named field sits inside a mirrored struct.</summary>
    internal struct FieldSpan
    {
        public int Offset;
        public int Size;
    }

    /// <summary>
    /// Where each field of a mirrored struct sits in memory, and which fields are settings rather than runtime
    /// state (timers, counters, learned values) that every game keeps for itself.
    /// </summary>
    internal sealed class StructLayoutInfo
    {
        private static readonly string[] RuntimeSubstrings =
        {
            "timer", "dynphase", "turnssince", "lowflow", "lowpriority", "carflow", "occupied", "weightedwaiting", "accum",
            "greenticks", "queuevehicles", "chainvehicles", "lastcalc", "lastflow", "lastavg", "lastqueue", "learned",
            "gotimestamp", "cached", "history",
        };

        private static readonly string[] RuntimeExact = new string[0];

        private struct Slot
        {
            public int Offset;
            public int Size;
            public bool Runtime;
            public bool IsEntity;
            public string RawName;
        }

        public int Size;
        public int FieldCount;
        public int RuntimeCount;
        public int PositionOffset = -1;
        public readonly List<int> RangeOffsets = new List<int>();
        public readonly List<int> RangeSizes = new List<int>();
        public readonly List<int> EntityOffsets = new List<int>();
        public readonly Dictionary<string, FieldSpan> Fields = new Dictionary<string, FieldSpan>();

        /// <summary>A layout for an empty marker component: nothing to read or compare.</summary>
        public static StructLayoutInfo Tag()
        {
            return new StructLayoutInfo { Size = 0 };
        }

        public FieldSpan Field(string name)
        {
            FieldSpan span;
            if (!Fields.TryGetValue(name, out span))
            {
                throw new InvalidOperationException("no field '" + name + "' in the mirrored struct");
            }

            return span;
        }

        public static StructLayoutInfo Build(Type type, TrackedType spec)
        {
            if (spec != null && spec.IsTag)
            {
                return Tag();
            }

            var info = new StructLayoutInfo { Size = UnsafeUtility.SizeOf(type) };
            var fields = new List<Slot>();
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.IsStatic)
                {
                    continue;
                }

                string name = Normalise(field.Name);
                fields.Add(new Slot
                {
                    Offset = UnsafeUtility.GetFieldOffset(field),
                    Size = SizeOf(field.FieldType),
                    Runtime = IsRuntime(name) || (spec != null && spec.RuntimeFields != null && Array.IndexOf(spec.RuntimeFields, name) >= 0),
                    IsEntity = field.FieldType == typeof(Entity),
                    RawName = field.Name,
                });
            }

            fields.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            for (int i = 0; i < fields.Count; i++)
            {
                Slot field = fields[i];
                if (field.Size <= 0)
                {
                    field.Size = (i + 1 < fields.Count ? fields[i + 1].Offset : info.Size) - field.Offset;
                }

                info.FieldCount++;
                info.Fields[field.RawName] = new FieldSpan { Offset = field.Offset, Size = field.Size };
                if (spec != null && spec.PositionField != null && field.RawName == spec.PositionField)
                {
                    info.PositionOffset = field.Offset;
                }

                if (field.Runtime)
                {
                    info.RuntimeCount++;
                    continue;
                }

                if (field.IsEntity)
                {
                    info.EntityOffsets.Add(field.Offset);
                }

                info.RangeOffsets.Add(field.Offset);
                info.RangeSizes.Add(field.Size);
            }

            return info;
        }

        /// <summary>A copy of <paramref name="raw"/> with every runtime field zeroed, for change detection.</summary>
        public byte[] Masked(byte[] raw)
        {
            var result = new byte[raw.Length];
            int count = Size > 0 ? raw.Length / Size : 0;
            for (int k = 0; k < count; k++)
            {
                int start = k * Size;
                for (int r = 0; r < RangeOffsets.Count; r++)
                {
                    Array.Copy(raw, start + RangeOffsets[r], result, start + RangeOffsets[r], RangeSizes[r]);
                }
            }

            return result;
        }

        /// <summary>The local bytes with the settings fields taken from the remote bytes; the remote element count wins.</summary>
        public byte[] Overlay(byte[] local, byte[] remote)
        {
            var result = new byte[remote.Length];
            int localCount = Size > 0 ? local.Length / Size : 0;
            int remoteCount = Size > 0 ? remote.Length / Size : 0;
            for (int k = 0; k < remoteCount; k++)
            {
                int start = k * Size;
                if (k >= localCount)
                {
                    Array.Copy(remote, start, result, start, Size);
                    continue;
                }

                Array.Copy(local, start, result, start, Size);
                for (int r = 0; r < RangeOffsets.Count; r++)
                {
                    Array.Copy(remote, start + RangeOffsets[r], result, start + RangeOffsets[r], RangeSizes[r]);
                }
            }

            return result;
        }

        private static string Normalise(string fieldName)
        {
            string name = fieldName;
            if (name.StartsWith("<", StringComparison.Ordinal))
            {
                int end = name.IndexOf('>');
                name = end > 1 ? name.Substring(1, end - 1) : name;
            }

            if (name.StartsWith("m_", StringComparison.Ordinal))
            {
                name = name.Substring(2);
            }

            return name.ToLowerInvariant();
        }

        private static bool IsRuntime(string name)
        {
            foreach (string exact in RuntimeExact)
            {
                if (name == exact)
                {
                    return true;
                }
            }

            foreach (string part in RuntimeSubstrings)
            {
                if (name.Contains(part))
                {
                    return true;
                }
            }

            return false;
        }

        private static int SizeOf(Type type)
        {
            try
            {
                if (type.IsEnum)
                {
                    type = Enum.GetUnderlyingType(type);
                }

                return UnsafeUtility.SizeOf(type);
            }
            catch (Exception)
            {
                return -1;
            }
        }
    }

    /// <summary>Raw struct bytes; both games run the same mod build, so the layout is the same on each side.</summary>
    internal static unsafe class RawBytes
    {
        public static byte[] Of<T>(ref T value) where T : unmanaged
        {
            var bytes = new byte[sizeof(T)];
            fixed (byte* p = bytes)
            {
                *(T*)p = value;
            }

            return bytes;
        }

        public static T To<T>(byte[] bytes, int offset) where T : unmanaged
        {
            fixed (byte* p = bytes)
            {
                return *(T*)(p + offset);
            }
        }

        public static void Put<T>(byte[] bytes, int offset, T value) where T : unmanaged
        {
            fixed (byte* p = bytes)
            {
                *(T*)(p + offset) = value;
            }
        }

        public static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal sealed class MirrorSnapshot
    {
        public byte[] Masked;
        public EntityRef Target;
    }

    /// <summary>One tracked type: reads and writes it on entities and remembers what was last seen or applied.</summary>
    internal abstract class Mirror
    {
        public TrackedType Spec;
        public StructLayoutInfo Layout;
        public EntityQuery Query;
        public readonly Dictionary<Entity, MirrorSnapshot> Last = new Dictionary<Entity, MirrorSnapshot>();
        public readonly Dictionary<Entity, int> Quiet = new Dictionary<Entity, int>();

        public string TypeName => Spec.TypeName;

        public int Stride => Layout.Size;

        public abstract bool Has(Entity entity);

        public abstract byte[] Read(Entity entity);

        public abstract void Write(Entity entity, byte[] data);

        public abstract void Remove(Entity entity);

        public float3 PositionOf(Entity entity)
        {
            if (Layout.PositionOffset < 0)
            {
                return float3.zero;
            }

            byte[] raw = Read(entity);
            return raw.Length >= Layout.PositionOffset + 12 ? RawBytes.To<float3>(raw, Layout.PositionOffset) : float3.zero;
        }

        public Entity FindByPosition(float3 position, float tolerance)
        {
            Entity best = Entity.Null;
            float bestDistance = float.MaxValue;
            using (NativeArray<Entity> entities = Query.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    if (Layout.PositionOffset < 0)
                    {
                        return entities[i];
                    }

                    float distance = math.distance(PositionOf(entities[i]).xz, position.xz);
                    if (distance <= tolerance && distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = entities[i];
                    }
                }
            }

            return best;
        }

        public void Forget()
        {
            Last.Clear();
            Quiet.Clear();
        }

        protected static EntityQuery MakeQuery(EntityManager entities, ComponentType type)
        {
            return entities.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { type },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
        }
    }

    internal sealed class ComponentMirror<T> : Mirror where T : unmanaged, IComponentData
    {
        private readonly EntityManager m_Entities;

        public ComponentMirror(EntityManager entities, TrackedType spec, StructLayoutInfo layout)
        {
            m_Entities = entities;
            Spec = spec;
            Layout = layout;
            Query = MakeQuery(entities, ComponentType.ReadOnly<T>());
        }

        public override bool Has(Entity entity)
        {
            return m_Entities.HasComponent<T>(entity);
        }

        public override byte[] Read(Entity entity)
        {
            T value = m_Entities.GetComponentData<T>(entity);
            return RawBytes.Of(ref value);
        }

        public override void Write(Entity entity, byte[] data)
        {
            T value = RawBytes.To<T>(data, 0);
            if (m_Entities.HasComponent<T>(entity))
            {
                m_Entities.SetComponentData(entity, value);
            }
            else
            {
                m_Entities.AddComponentData(entity, value);
            }
        }

        public override void Remove(Entity entity)
        {
            m_Entities.RemoveComponent<T>(entity);
        }
    }

    /// <summary>An empty marker component: mirrored as present or absent, never read (zero-sized components cannot be).</summary>
    internal sealed class TagMirror<T> : Mirror where T : unmanaged, IComponentData
    {
        private static readonly byte[] Nothing = new byte[0];
        private readonly EntityManager m_Entities;

        public TagMirror(EntityManager entities, TrackedType spec, StructLayoutInfo layout)
        {
            m_Entities = entities;
            Spec = spec;
            Layout = layout;
            Query = MakeQuery(entities, ComponentType.ReadOnly<T>());
        }

        public override bool Has(Entity entity)
        {
            return m_Entities.HasComponent<T>(entity);
        }

        public override byte[] Read(Entity entity)
        {
            return Nothing;
        }

        public override void Write(Entity entity, byte[] data)
        {
            if (!m_Entities.HasComponent<T>(entity))
            {
                m_Entities.AddComponent<T>(entity);
            }
        }

        public override void Remove(Entity entity)
        {
            m_Entities.RemoveComponent<T>(entity);
        }
    }

    /// <summary>Builds the right mirror for a type found at runtime.</summary>
    internal static class MirrorFactory
    {
        public static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type = assembly.GetType(fullName, false);
                    if (type != null)
                    {
                        return type;
                    }
                }
                catch (Exception)
                {
                    // Dynamic or broken assembly; not ours.
                }
            }

            return null;
        }

        public static Mirror Create(EntityManager entities, TrackedType spec, Type type)
        {
            StructLayoutInfo layout = StructLayoutInfo.Build(type, spec);
            Type open = spec.IsTag ? typeof(TagMirror<>) : spec.IsBuffer ? typeof(BufferMirror<>) : typeof(ComponentMirror<>);
            return (Mirror)Activator.CreateInstance(open.MakeGenericType(type), entities, spec, layout);
        }
    }

    internal sealed class BufferMirror<T> : Mirror where T : unmanaged, IBufferElementData
    {
        private readonly EntityManager m_Entities;

        public BufferMirror(EntityManager entities, TrackedType spec, StructLayoutInfo layout)
        {
            m_Entities = entities;
            Spec = spec;
            Layout = layout;
            Query = MakeQuery(entities, ComponentType.ReadOnly<T>());
        }

        public override bool Has(Entity entity)
        {
            return m_Entities.HasBuffer<T>(entity);
        }

        public override byte[] Read(Entity entity)
        {
            DynamicBuffer<T> buffer = m_Entities.GetBuffer<T>(entity, true);
            int stride = Layout.Size;
            var bytes = new byte[buffer.Length * stride];
            for (int i = 0; i < buffer.Length; i++)
            {
                T value = buffer[i];
                Array.Copy(RawBytes.Of(ref value), 0, bytes, i * stride, stride);
            }

            return bytes;
        }

        public override void Write(Entity entity, byte[] data)
        {
            DynamicBuffer<T> buffer = m_Entities.HasBuffer<T>(entity) ? m_Entities.GetBuffer<T>(entity) : m_Entities.AddBuffer<T>(entity);
            buffer.Clear();
            int stride = Layout.Size;
            int count = stride > 0 ? data.Length / stride : 0;
            for (int i = 0; i < count; i++)
            {
                buffer.Add(RawBytes.To<T>(data, i * stride));
            }
        }

        public override void Remove(Entity entity)
        {
            m_Entities.RemoveComponent<T>(entity);
        }
    }

    /// <summary>
    /// Keeps other mods' per-entity settings the same for everyone. Traffic Tool Essentials, for example, stores a
    /// junction's signal pattern as its own components on the node; the game's build sync never sees those. This
    /// system walks the tracked component types once a second, sends the raw bytes of anything that changed
    /// (target and any entity references inside described by prefab and position, as build commands do) and
    /// writes what arrives from other players onto the matching entity here, then flags it Updated so the owning
    /// mod re-initialises it exactly as after a local click. Runtime fields (timers, counters, learned values)
    /// are left alone in both directions. Nothing is sent for a type unless the mod that owns it is loaded here.
    /// </summary>
    public partial class ModDataSyncSystem : GameSystemBase
    {
        private const int PassInterval = 60;
        private const int QuietPasses = 3;
        private const int RetryTypesEvery = 300;
        private const float ModEntityTolerance = 5f;
        private const int MaxMessageBytes = 256 * 1024;
        private const long ComplaintIntervalMs = 300000;

        private static readonly TrackedType[] Tracked =
        {
            // Traffic Tool Essentials: junction signals, phases, per-approach and per-lane groups, labels, sync groups, depot zones.
            new TrackedType { TypeName = "C2VM.TrafficToolEssentials.Components.CustomTrafficLights" },
            new TrackedType { TypeName = "C2VM.TrafficToolEssentials.Components.CustomPhaseData", IsBuffer = true, RuntimeFields = new[] { "priority", "targetduration" } },
            new TrackedType { TypeName = "C2VM.TrafficToolEssentials.Components.EdgeGroupMask", IsBuffer = true },
            new TrackedType { TypeName = "C2VM.TrafficToolEssentials.Components.SubLaneGroupMask", IsBuffer = true },
            new TrackedType { TypeName = "C2VM.TrafficToolEssentials.Components.ExtraLaneSignal" },
            new TrackedType { TypeName = "C2VM.TrafficToolEssentials.Components.IntersectionCustomLabel", MarkUpdated = false },
            new TrackedType { TypeName = "C2VM.TrafficToolEssentials.Components.LineCustomLabel", MarkUpdated = false },
            new TrackedType { TypeName = "C2VM.TrafficToolEssentials.Components.SyncGroupManager", OwnsEntity = true, MarkUpdated = false },
            new TrackedType { TypeName = "C2VM.TrafficToolEssentials.Components.SyncGroup", IsBuffer = true, MarkUpdated = false },
            new TrackedType { TypeName = "C2VM.TrafficToolEssentials.Components.DepotZoneData", OwnsEntity = true, PositionField = "m_Centroid", MarkUpdated = false },
            new TrackedType { TypeName = "C2VM.TrafficToolEssentials.Components.DepotZonePoint", IsBuffer = true, MarkUpdated = false },
            new TrackedType { TypeName = "C2VM.TrafficToolEssentials.Components.LineDepotZoneAssignment", MarkUpdated = false },
            // Shared lane library (Traffic Tool Essentials / Traffic Lights Enhancement): lane direction restrictions.
            new TrackedType { TypeName = "C2VM.CommonLibraries.LaneSystem.CustomLaneDirection", IsBuffer = true },
            // Traffic (TM:PE successor): priority signs live on the road edge as a small buffer plus a marker.
            // Its lane connections need hidden data entities and go through TrafficLaneSyncSystem instead.
            new TrackedType { TypeName = "Traffic.Components.ModifiedPriorities", IsTag = true },
            new TrackedType { TypeName = "Traffic.Components.PrioritySigns.LanePriority", IsBuffer = true },
        };

        private readonly List<ModDataCommand> m_Incoming = new List<ModDataCommand>();
        private readonly List<Mirror> m_Mirrors = new List<Mirror>();
        private readonly List<TrackedType> m_Unbound = new List<TrackedType>(Tracked);
        private readonly Dictionary<string, long> m_Complaints = new Dictionary<string, long>();
        private EntityResolver m_Resolver;
        private int m_Frame;
        private bool m_Primed;
        private bool m_ReportedMissing;
        private int m_Sent;
        private int m_Applied;
        private int m_Failed;

        public int SentCount => m_Sent;

        public int AppliedCount => m_Applied;

        public int FailedCount => m_Failed;

        public int TrackedTypeCount => m_Mirrors.Count;

        public void Receive(ModDataCommand command)
        {
            m_Incoming.Add(command);
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Resolver = new EntityResolver(World, World.GetOrCreateSystemManaged<PrefabSystem>());
            BindTypes();
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            GameManager manager = GameManager.instance;
            if (service == null || service.Session.State != SessionState.Connected || manager == null || manager.gameMode != GameMode.Game || manager.isGameLoading)
            {
                if (m_Primed)
                {
                    Forget();
                }

                m_Incoming.Clear();
                return;
            }

            if (m_Incoming.Count > 0)
            {
                ApplyIncoming();
            }

            m_Frame++;
            if (m_Unbound.Count > 0 && (m_Frame == 1 || m_Frame % RetryTypesEvery == 0))
            {
                // Other mods load after this one, so their types are looked up once in a city and again now and then.
                BindTypes();
            }

            if (m_Frame % PassInterval != 0 || m_Mirrors.Count == 0)
            {
                return;
            }

            bool send = m_Primed;
            int tracked = 0;
            foreach (Mirror mirror in m_Mirrors)
            {
                try
                {
                    Scan(mirror, send, service);
                }
                catch (Exception ex)
                {
                    Complain("scan " + mirror.TypeName, "Mod data sync: scanning " + mirror.TypeName + " failed: " + ex.Message);
                }

                tracked += mirror.Last.Count;
            }

            if (!m_Primed)
            {
                m_Primed = true;
                Mod.log.Info("Mod data sync primed: " + m_Mirrors.Count + " types, " + tracked + " entities carry them");
            }
        }

        // ------------------------------------------------------------------ types

        private void BindTypes()
        {
            for (int i = m_Unbound.Count - 1; i >= 0; i--)
            {
                TrackedType spec = m_Unbound[i];
                Type type = MirrorFactory.FindType(spec.TypeName);
                if (type == null)
                {
                    continue;
                }

                m_Unbound.RemoveAt(i);
                try
                {
                    Mirror mirror = MirrorFactory.Create(EntityManager, spec, type);
                    m_Mirrors.Add(mirror);
                    StructLayoutInfo layout = mirror.Layout;
                    Mod.log.Info("Mod data sync: tracking " + spec.TypeName + (spec.IsTag ? " (marker)" : " (" + layout.Size + " bytes, " + layout.FieldCount + " fields, " + layout.RuntimeCount + " runtime, " + layout.EntityOffsets.Count + " entity refs)"));
                }
                catch (Exception ex)
                {
                    Mod.log.Warn("Mod data sync: cannot track " + spec.TypeName + ": " + ex.Message);
                }
            }

            m_Mirrors.Sort((a, b) => Array.IndexOf(Tracked, a.Spec).CompareTo(Array.IndexOf(Tracked, b.Spec)));
            if (m_Unbound.Count > 0 && !m_ReportedMissing && m_Frame > 0)
            {
                m_ReportedMissing = true;
                var names = new List<string>();
                foreach (TrackedType spec in m_Unbound)
                {
                    names.Add(spec.TypeName.Substring(spec.TypeName.LastIndexOf('.') + 1));
                }

                Mod.log.Info("Mod data sync: not present in this game (mod not loaded), skipped: " + string.Join(", ", names.ToArray()));
            }
        }

        private Mirror FindMirror(string typeName)
        {
            foreach (Mirror mirror in m_Mirrors)
            {
                if (mirror.TypeName == typeName)
                {
                    return mirror;
                }
            }

            return null;
        }

        // ------------------------------------------------------------------ outgoing

        private void Scan(Mirror mirror, bool send, MultiplayerService service)
        {
            var seen = new HashSet<Entity>();
            using (NativeArray<Entity> entities = mirror.Query.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    byte[] raw;
                    try
                    {
                        raw = mirror.Read(entity);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    seen.Add(entity);
                    byte[] masked = mirror.Layout.Masked(raw);
                    MirrorSnapshot previous;
                    if (mirror.Last.TryGetValue(entity, out previous) && RawBytes.Same(previous.Masked, masked))
                    {
                        continue;
                    }

                    EntityRef target = DescribeTarget(entity);
                    mirror.Last[entity] = new MirrorSnapshot { Masked = masked, Target = target };
                    if (!send || IsQuiet(mirror, entity))
                    {
                        continue;
                    }

                    if (target.Kind == EntityKind.None || target.Kind == EntityKind.Unresolvable)
                    {
                        Complain("target " + mirror.TypeName, "Mod data sync: " + Short(mirror.TypeName) + " sits on an entity that cannot be described for other players; not synced");
                        continue;
                    }

                    string why;
                    ModDataCommand command = Build(mirror, target, raw, out why);
                    if (command == null)
                    {
                        Complain("build " + mirror.TypeName, "Mod data sync: " + Short(mirror.TypeName) + " on " + target + " not synced: " + why);
                        continue;
                    }

                    service.SendModData(command);
                    m_Sent++;
                }
            }

            var gone = new List<Entity>();
            foreach (KeyValuePair<Entity, MirrorSnapshot> pair in mirror.Last)
            {
                if (!seen.Contains(pair.Key))
                {
                    gone.Add(pair.Key);
                }
            }

            foreach (Entity entity in gone)
            {
                MirrorSnapshot snapshot = mirror.Last[entity];
                mirror.Last.Remove(entity);
                if (!send || IsQuiet(mirror, entity))
                {
                    continue;
                }

                bool exists = EntityManager.Exists(entity) && !EntityManager.HasComponent<Deleted>(entity);
                if (!exists && !mirror.Spec.OwnsEntity)
                {
                    // The road or line itself went; the build sync carries that.
                    continue;
                }

                EntityRef target = exists ? DescribeTarget(entity) : snapshot.Target;
                if (target == null || target.Kind == EntityKind.None || target.Kind == EntityKind.Unresolvable)
                {
                    continue;
                }

                service.SendModData(new ModDataCommand { Target = target, TypeName = mirror.TypeName, IsBuffer = mirror.Spec.IsBuffer, Remove = true, ElementSize = mirror.Stride });
                m_Sent++;
            }

            if (mirror.Quiet.Count > 0)
            {
                var keys = new List<Entity>(mirror.Quiet.Keys);
                foreach (Entity entity in keys)
                {
                    int left = mirror.Quiet[entity] - 1;
                    if (left <= 0)
                    {
                        mirror.Quiet.Remove(entity);
                    }
                    else
                    {
                        mirror.Quiet[entity] = left;
                    }
                }
            }
        }

        private ModDataCommand Build(Mirror mirror, EntityRef target, byte[] raw, out string why)
        {
            why = null;
            if (raw.Length > MaxMessageBytes)
            {
                why = raw.Length + " bytes is too much for one message";
                return null;
            }

            var command = new ModDataCommand
            {
                Target = target,
                TypeName = mirror.TypeName,
                IsBuffer = mirror.Spec.IsBuffer,
                ElementSize = mirror.Stride,
                Data = raw,
            };

            int stride = mirror.Stride;
            int count = stride > 0 ? raw.Length / stride : 0;
            for (int k = 0; k < count; k++)
            {
                foreach (int offset in mirror.Layout.EntityOffsets)
                {
                    int at = k * stride + offset;
                    Entity inner = RawBytes.To<Entity>(raw, at);
                    if (inner == Entity.Null)
                    {
                        continue;
                    }

                    EntityRef reference = DescribeTarget(inner);
                    if (reference.Kind == EntityKind.None || reference.Kind == EntityKind.Unresolvable)
                    {
                        why = "it refers to an entity of a kind that cannot be described (" + inner + ")";
                        return null;
                    }

                    command.Entities.Add(new EntityPatch { Offset = at, Ref = reference });
                }
            }

            return command;
        }

        private EntityRef DescribeTarget(Entity entity)
        {
            EntityRef reference = m_Resolver.Describe(entity);
            if (reference.Kind != EntityKind.Unresolvable)
            {
                return reference;
            }

            foreach (Mirror mirror in m_Mirrors)
            {
                if (mirror.Spec.OwnsEntity && mirror.Has(entity))
                {
                    return new EntityRef
                    {
                        Kind = EntityKind.ModEntity,
                        Prefab = new PrefabKey { Type = "mod", Name = mirror.TypeName },
                        Position = EntityResolver.ToVec(mirror.PositionOf(entity)),
                    };
                }
            }

            return reference;
        }

        // ------------------------------------------------------------------ incoming

        private void ApplyIncoming()
        {
            var batch = new List<ModDataCommand>(m_Incoming);
            m_Incoming.Clear();
            foreach (ModDataCommand command in batch)
            {
                string failure = null;
                try
                {
                    failure = Apply(command);
                }
                catch (Exception ex)
                {
                    failure = ex.GetType().Name + ": " + ex.Message;
                }

                if (failure != null)
                {
                    m_Failed++;
                    Mod.log.Warn("Mod data sync: could not apply " + command + ": " + failure);
                }
                else
                {
                    m_Applied++;
                    Mod.log.Info("Mod data sync: applied " + command);
                }
            }
        }

        /// <summary>Null when applied; otherwise why not.</summary>
        private string Apply(ModDataCommand command)
        {
            Mirror mirror = FindMirror(command.TypeName);
            if (mirror == null)
            {
                return m_Unbound.Exists(t => t.TypeName == command.TypeName) ? "that mod is not loaded here" : "unknown type";
            }

            if (!command.Remove && command.ElementSize != mirror.Stride)
            {
                return "struct size differs (theirs " + command.ElementSize + ", ours " + mirror.Stride + " bytes): different versions of that mod";
            }

            string why;
            Entity entity = ResolveTarget(command.Target, !command.Remove && mirror.Spec.OwnsEntity, out why);
            if (entity == Entity.Null)
            {
                return why ?? "target not found here";
            }

            if (command.Remove)
            {
                if (mirror.Spec.OwnsEntity)
                {
                    foreach (Mirror other in m_Mirrors)
                    {
                        other.Last.Remove(entity);
                        other.Quiet.Remove(entity);
                    }

                    EntityManager.DestroyEntity(entity);
                    return null;
                }

                if (mirror.Has(entity))
                {
                    mirror.Remove(entity);
                }

                mirror.Last.Remove(entity);
            }
            else
            {
                byte[] data = (byte[])command.Data.Clone();
                if (!command.IsBuffer && data.Length != mirror.Stride)
                {
                    return "expected " + mirror.Stride + " bytes, got " + data.Length;
                }

                if (command.IsBuffer && mirror.Stride > 0 && data.Length % mirror.Stride != 0)
                {
                    return data.Length + " bytes is not a whole number of " + mirror.Stride + "-byte elements";
                }

                foreach (EntityPatch patch in command.Entities)
                {
                    if (patch.Offset < 0 || patch.Offset + 8 > data.Length)
                    {
                        return "entity reference outside the data";
                    }

                    Entity inner = ResolveTarget(patch.Ref, false, out why);
                    if (inner == Entity.Null && patch.Ref.Kind != EntityKind.None)
                    {
                        return "referenced " + patch.Ref + " not found here" + (why != null ? " (" + why + ")" : "");
                    }

                    RawBytes.Put(data, patch.Offset, inner);
                }

                byte[] merged = mirror.Has(entity) ? mirror.Layout.Overlay(mirror.Read(entity), data) : data;
                mirror.Write(entity, merged);
                mirror.Last[entity] = new MirrorSnapshot { Masked = mirror.Layout.Masked(merged), Target = command.Target };
            }

            mirror.Quiet[entity] = QuietPasses;
            if (mirror.Spec.MarkUpdated && EntityManager.Exists(entity) && !EntityManager.HasComponent<Updated>(entity))
            {
                EntityManager.AddComponent<Updated>(entity);
            }

            return null;
        }

        private Entity ResolveTarget(EntityRef reference, bool createIfMissing, out string why)
        {
            why = null;
            if (reference == null || reference.Kind == EntityKind.None)
            {
                return Entity.Null;
            }

            if (reference.Kind != EntityKind.ModEntity)
            {
                return m_Resolver.Resolve(reference, out why);
            }

            Mirror identity = FindMirror(reference.Prefab.Name);
            if (identity == null)
            {
                why = "no mirror for " + reference.Prefab.Name;
                return Entity.Null;
            }

            Entity found = identity.FindByPosition(EntityResolver.ToFloat3(reference.Position), ModEntityTolerance);
            if (found == Entity.Null && createIfMissing)
            {
                found = EntityManager.CreateEntity();
            }

            if (found == Entity.Null)
            {
                why = "no " + Short(reference.Prefab.Name) + " entity near " + reference.Position;
            }

            return found;
        }

        // ------------------------------------------------------------------ dev

        /// <summary>Dev trigger: give one junction with traffic lights the "Mod default" Traffic Tool Essentials pattern.</summary>
        public string DevSetTestPattern()
        {
            Mirror mirror = FindMirror("C2VM.TrafficToolEssentials.Components.CustomTrafficLights");
            if (mirror == null)
            {
                return "Traffic Tool Essentials is not loaded here";
            }

            Entity node = Entity.Null;
            EntityQuery junctions = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Net.Node>(), ComponentType.ReadOnly<Game.Net.TrafficLights>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            using (NativeArray<Entity> entities = junctions.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    node = entities[i];
                    if (!mirror.Has(node))
                    {
                        break;
                    }
                }
            }

            if (node == Entity.Null)
            {
                return "no junction with traffic lights in this city";
            }

            Type type = mirror.GetType().GetGenericArguments()[0];
            object value = Activator.CreateInstance(type);
            MethodInfo setPattern = type.GetMethod("SetPattern", new[] { typeof(uint) });
            if (setPattern == null)
            {
                return "CustomTrafficLights has no SetPattern(uint)";
            }

            setPattern.Invoke(value, new object[] { 4u });
            object[] args = { value };
            var bytes = (byte[])typeof(RawBytes).GetMethod("Of").MakeGenericMethod(type).Invoke(null, args);
            mirror.Write(node, bytes);
            if (!EntityManager.HasComponent<Updated>(node))
            {
                EntityManager.AddComponent<Updated>(node);
            }

            return "set pattern ModDefault on the junction at " + EntityManager.GetComponentData<Game.Net.Node>(node).m_Position + " (" + bytes.Length + " bytes); the next pass sends it";
        }

        // ------------------------------------------------------------------ housekeeping

        private void Forget()
        {
            m_Primed = false;
            foreach (Mirror mirror in m_Mirrors)
            {
                mirror.Forget();
            }
        }

        private static bool IsQuiet(Mirror mirror, Entity entity)
        {
            int left;
            return mirror.Quiet.TryGetValue(entity, out left) && left > 0;
        }

        private void Complain(string key, string text)
        {
            long now = Mod.Service != null ? Mod.Service.NowMs : 0;
            long last;
            if (m_Complaints.TryGetValue(key, out last) && now - last < ComplaintIntervalMs)
            {
                return;
            }

            m_Complaints[key] = now;
            Mod.log.Warn(text);
        }

        private static string Short(string typeName)
        {
            return typeName.Substring(typeName.LastIndexOf('.') + 1);
        }
    }
}
