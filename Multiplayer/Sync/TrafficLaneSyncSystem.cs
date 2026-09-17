using System;
using System.Collections.Generic;
using System.Reflection;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.SceneFlow;
using Multiplayer.Core.Build;
using Multiplayer.Core.Session;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Multiplayer.Sync
{
    /// <summary>
    /// Keeps the Traffic mod's custom lane connections the same for everyone. Traffic stores them as a buffer on
    /// the junction node (one group per modified lane) where every group points at a hidden data entity that
    /// holds the generated connections and a DataOwner back-reference. The generic mod data sync cannot name
    /// such hidden entities, so this system spells the whole structure out field by field
    /// (<see cref="LaneConnectionsCommand"/>), and on the receiving side recreates the data entities, rewrites
    /// the node's buffer and flags the node Updated so Traffic's lane system rebuilds the lanes as after a click.
    /// Field offsets come from the loaded Traffic assembly, so a Traffic update does not break the wire format.
    /// </summary>
    public partial class TrafficLaneSyncSystem : GameSystemBase
    {
        private const int PassInterval = 60;
        private const int QuietPasses = 3;
        private const int RetryTypesEvery = 300;
        private const long ComplaintIntervalMs = 300000;

        private const string GroupsType = "Traffic.Components.LaneConnections.ModifiedLaneConnections";
        private const string ConnectionsType = "Traffic.Components.LaneConnections.GeneratedConnection";
        private const string OwnerType = "Traffic.Components.DataOwner";
        private const string TagType = "Traffic.Components.ModifiedConnections";
        private const string DefaultsType = "Traffic.Systems.ModDefaultsSystem";

        private readonly List<LaneConnectionsCommand> m_Incoming = new List<LaneConnectionsCommand>();
        private readonly Dictionary<Entity, byte[]> m_Last = new Dictionary<Entity, byte[]>();
        private readonly Dictionary<Entity, EntityRef> m_LastTarget = new Dictionary<Entity, EntityRef>();
        private readonly Dictionary<Entity, int> m_Quiet = new Dictionary<Entity, int>();
        private readonly Dictionary<string, long> m_Complaints = new Dictionary<string, long>();
        private EntityResolver m_Resolver;
        private Mirror m_Groups;
        private Mirror m_Connections;
        private Mirror m_Owner;
        private Mirror m_Tag;
        private FieldInfo m_FakePrefabField;
        private bool m_Bound;
        private bool m_ReportedMissing;
        private bool m_Primed;
        private int m_Frame;
        private int m_Sent;
        private int m_Applied;
        private int m_Failed;

        public bool IsBound => m_Bound;

        public int SentCount => m_Sent;

        public int AppliedCount => m_Applied;

        public int FailedCount => m_Failed;

        public void Receive(LaneConnectionsCommand command)
        {
            m_Incoming.Add(command);
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Resolver = new EntityResolver(World, World.GetOrCreateSystemManaged<PrefabSystem>());
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

            m_Frame++;
            if (!m_Bound && (m_Frame == 1 || m_Frame % RetryTypesEvery == 0))
            {
                Bind();
            }

            if (!m_Bound)
            {
                m_Incoming.Clear();
                return;
            }

            if (m_Incoming.Count > 0)
            {
                ApplyIncoming();
            }

            if (m_Frame % PassInterval != 0)
            {
                return;
            }

            try
            {
                Scan(m_Primed, service);
            }
            catch (Exception ex)
            {
                Complain("scan", "Lane connection sync: scanning failed: " + ex.Message);
            }

            if (!m_Primed)
            {
                m_Primed = true;
                Mod.log.Info("Lane connection sync primed: " + m_Last.Count + " junctions carry custom connections");
            }
        }

        // ------------------------------------------------------------------ binding

        private void Bind()
        {
            Type groups = MirrorFactory.FindType(GroupsType);
            Type connections = MirrorFactory.FindType(ConnectionsType);
            Type owner = MirrorFactory.FindType(OwnerType);
            Type tag = MirrorFactory.FindType(TagType);
            Type defaults = MirrorFactory.FindType(DefaultsType);
            if (groups == null || connections == null || owner == null || tag == null)
            {
                if (!m_ReportedMissing)
                {
                    m_ReportedMissing = true;
                    Mod.log.Info("Lane connection sync: the Traffic mod is not loaded here; skipped");
                }

                return;
            }

            try
            {
                m_Groups = MirrorFactory.Create(EntityManager, new TrackedType { TypeName = GroupsType, IsBuffer = true }, groups);
                m_Connections = MirrorFactory.Create(EntityManager, new TrackedType { TypeName = ConnectionsType, IsBuffer = true }, connections);
                m_Owner = MirrorFactory.Create(EntityManager, new TrackedType { TypeName = OwnerType }, owner);
                m_Tag = MirrorFactory.Create(EntityManager, new TrackedType { TypeName = TagType, IsTag = true }, tag);
                m_FakePrefabField = defaults != null ? defaults.GetField("FakePrefabRef", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) : null;

                // Every field this system reads or writes must exist; a renamed field is reported once, not guessed at.
                foreach (string name in new[] { "laneIndex", "carriagewayAndGroup", "lanePosition", "edgeEntity", "modifiedConnections" })
                {
                    m_Groups.Layout.Field(name);
                }

                foreach (string name in new[] { "sourceEntity", "targetEntity", "laneIndexMap", "carriagewayAndGroupIndexMap", "lanePositionMap", "method", "isUnsafe" })
                {
                    m_Connections.Layout.Field(name);
                }

                m_Owner.Layout.Field("entity");
                m_Bound = true;
                Mod.log.Info("Lane connection sync: tracking Traffic's lane connections (group " + m_Groups.Stride + " bytes, connection " + m_Connections.Stride + " bytes"
                    + (m_FakePrefabField != null ? ", fake prefab known" : ", fake prefab unknown") + ")");
            }
            catch (Exception ex)
            {
                m_ReportedMissing = true;
                Mod.log.Warn("Lane connection sync: cannot track Traffic's lane connections: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ outgoing

        private void Scan(bool send, MultiplayerService service)
        {
            var seen = new HashSet<Entity>();
            using (NativeArray<Entity> nodes = m_Groups.Query.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < nodes.Length; i++)
                {
                    Entity node = nodes[i];
                    seen.Add(node);
                    string why;
                    LaneConnectionsCommand command = Describe(node, out why);
                    if (command == null)
                    {
                        Complain("describe", "Lane connection sync: a junction's connections cannot be described for other players: " + why);
                        continue;
                    }

                    byte[] bytes = command.ToBytes();
                    byte[] previous;
                    if (m_Last.TryGetValue(node, out previous) && RawBytes.Same(previous, bytes))
                    {
                        continue;
                    }

                    m_Last[node] = bytes;
                    m_LastTarget[node] = command.Node;
                    if (!send || IsQuiet(node))
                    {
                        continue;
                    }

                    service.SendLaneConnections(command);
                    m_Sent++;
                }
            }

            var gone = new List<Entity>();
            foreach (Entity node in m_Last.Keys)
            {
                if (!seen.Contains(node))
                {
                    gone.Add(node);
                }
            }

            foreach (Entity node in gone)
            {
                EntityRef target;
                m_LastTarget.TryGetValue(node, out target);
                m_Last.Remove(node);
                m_LastTarget.Remove(node);
                if (!send || IsQuiet(node) || !EntityManager.Exists(node) || EntityManager.HasComponent<Deleted>(node) || target == null)
                {
                    // The junction itself went, or we are echoing: the build sync carries that.
                    continue;
                }

                service.SendLaneConnections(new LaneConnectionsCommand { Node = target, Remove = true });
                m_Sent++;
            }

            if (m_Quiet.Count > 0)
            {
                var keys = new List<Entity>(m_Quiet.Keys);
                foreach (Entity node in keys)
                {
                    int left = m_Quiet[node] - 1;
                    if (left <= 0)
                    {
                        m_Quiet.Remove(node);
                    }
                    else
                    {
                        m_Quiet[node] = left;
                    }
                }
            }
        }

        /// <summary>The junction's custom connections as a command, or null (with a reason) when something in it cannot be named.</summary>
        private LaneConnectionsCommand Describe(Entity node, out string why)
        {
            why = null;
            EntityRef nodeRef = m_Resolver.Describe(node);
            if (nodeRef.Kind != EntityKind.NetNode)
            {
                why = "the junction is not a road node";
                return null;
            }

            var command = new LaneConnectionsCommand { Node = nodeRef };
            byte[] raw = m_Groups.Read(node);
            int stride = m_Groups.Stride;
            StructLayoutInfo layout = m_Groups.Layout;
            int count = stride > 0 ? raw.Length / stride : 0;
            for (int k = 0; k < count; k++)
            {
                int at = k * stride;
                var group = new LaneGroupData
                {
                    LaneIndex = RawBytes.To<int>(raw, at + layout.Field("laneIndex").Offset),
                    CarriagewayX = RawBytes.To<int>(raw, at + layout.Field("carriagewayAndGroup").Offset),
                    CarriagewayY = RawBytes.To<int>(raw, at + layout.Field("carriagewayAndGroup").Offset + 4),
                    LanePosition = EntityResolver.ToVec(RawBytes.To<float3>(raw, at + layout.Field("lanePosition").Offset)),
                };

                Entity edge = RawBytes.To<Entity>(raw, at + layout.Field("edgeEntity").Offset);
                group.Edge = m_Resolver.Describe(edge);
                if (group.Edge.Kind != EntityKind.NetEdge)
                {
                    why = "a group's edge cannot be named";
                    return null;
                }

                Entity data = RawBytes.To<Entity>(raw, at + layout.Field("modifiedConnections").Offset);
                if (data != Entity.Null && EntityManager.Exists(data) && m_Connections.Has(data))
                {
                    byte[] rawConnections = m_Connections.Read(data);
                    int cStride = m_Connections.Stride;
                    StructLayoutInfo cLayout = m_Connections.Layout;
                    int cCount = cStride > 0 ? rawConnections.Length / cStride : 0;
                    for (int c = 0; c < cCount; c++)
                    {
                        int cAt = c * cStride;
                        var connection = new LaneConnectionData
                        {
                            Source = m_Resolver.Describe(RawBytes.To<Entity>(rawConnections, cAt + cLayout.Field("sourceEntity").Offset)),
                            Target = m_Resolver.Describe(RawBytes.To<Entity>(rawConnections, cAt + cLayout.Field("targetEntity").Offset)),
                            LaneIndexX = RawBytes.To<int>(rawConnections, cAt + cLayout.Field("laneIndexMap").Offset),
                            LaneIndexY = RawBytes.To<int>(rawConnections, cAt + cLayout.Field("laneIndexMap").Offset + 4),
                            PositionA = EntityResolver.ToVec(RawBytes.To<float3>(rawConnections, cAt + cLayout.Field("lanePositionMap").Offset)),
                            PositionB = EntityResolver.ToVec(RawBytes.To<float3>(rawConnections, cAt + cLayout.Field("lanePositionMap").Offset + 12)),
                            Method = ReadSmallInt(rawConnections, cAt + cLayout.Field("method").Offset, cLayout.Field("method").Size),
                            IsUnsafe = rawConnections[cAt + cLayout.Field("isUnsafe").Offset] != 0,
                        };
                        for (int m = 0; m < 4; m++)
                        {
                            connection.GroupMap[m] = RawBytes.To<int>(rawConnections, cAt + cLayout.Field("carriagewayAndGroupIndexMap").Offset + 4 * m);
                        }

                        if (connection.Source.Kind != EntityKind.NetEdge || connection.Target.Kind != EntityKind.NetEdge)
                        {
                            why = "a connection's edge cannot be named";
                            return null;
                        }

                        group.Connections.Add(connection);
                    }
                }

                command.Groups.Add(group);
            }

            return command;
        }

        // ------------------------------------------------------------------ incoming

        private void ApplyIncoming()
        {
            var batch = new List<LaneConnectionsCommand>(m_Incoming);
            m_Incoming.Clear();
            foreach (LaneConnectionsCommand command in batch)
            {
                string failure;
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
                    Mod.log.Warn("Lane connection sync: could not apply " + command + ": " + failure);
                }
                else
                {
                    m_Applied++;
                    Mod.log.Info("Lane connection sync: applied " + command);
                }
            }
        }

        /// <summary>Null when applied; otherwise why not.</summary>
        private string Apply(LaneConnectionsCommand command)
        {
            string why;
            Entity node = m_Resolver.Resolve(command.Node, out why);
            if (node == Entity.Null)
            {
                return why ?? "junction not found here";
            }

            // Whatever this junction had is replaced wholesale, data entities included (Traffic marks them Deleted too).
            if (m_Groups.Has(node))
            {
                byte[] old = m_Groups.Read(node);
                int stride = m_Groups.Stride;
                int oldCount = stride > 0 ? old.Length / stride : 0;
                for (int k = 0; k < oldCount; k++)
                {
                    Entity data = RawBytes.To<Entity>(old, k * stride + m_Groups.Layout.Field("modifiedConnections").Offset);
                    if (data != Entity.Null && EntityManager.Exists(data) && !EntityManager.HasComponent<Deleted>(data))
                    {
                        EntityManager.AddComponent<Deleted>(data);
                    }
                }
            }

            if (command.Remove || command.Groups.Count == 0)
            {
                if (m_Groups.Has(node))
                {
                    m_Groups.Remove(node);
                }

                if (m_Tag.Has(node))
                {
                    m_Tag.Remove(node);
                }

                m_Last.Remove(node);
                m_LastTarget.Remove(node);
            }
            else
            {
                Entity fakePrefab = m_FakePrefabField != null && m_FakePrefabField.GetValue(null) is Entity ? (Entity)m_FakePrefabField.GetValue(null) : Entity.Null;
                var resolvedGroups = new List<byte[]>();
                foreach (LaneGroupData group in command.Groups)
                {
                    Entity edge = m_Resolver.Resolve(group.Edge, out why);
                    if (edge == Entity.Null)
                    {
                        return "edge " + group.Edge + " not found here" + (why != null ? " (" + why + ")" : "");
                    }

                    // The hidden data entity: owner back-reference, the generated connections, Traffic's fake prefab.
                    byte[] connectionBytes = new byte[group.Connections.Count * m_Connections.Stride];
                    StructLayoutInfo cLayout = m_Connections.Layout;
                    for (int c = 0; c < group.Connections.Count; c++)
                    {
                        LaneConnectionData connection = group.Connections[c];
                        Entity source = m_Resolver.Resolve(connection.Source, out why);
                        Entity target = m_Resolver.Resolve(connection.Target, out why);
                        if (source == Entity.Null || target == Entity.Null)
                        {
                            return "a connection's edge is not found here" + (why != null ? " (" + why + ")" : "");
                        }

                        int at = c * m_Connections.Stride;
                        RawBytes.Put(connectionBytes, at + cLayout.Field("sourceEntity").Offset, source);
                        RawBytes.Put(connectionBytes, at + cLayout.Field("targetEntity").Offset, target);
                        RawBytes.Put(connectionBytes, at + cLayout.Field("laneIndexMap").Offset, connection.LaneIndexX);
                        RawBytes.Put(connectionBytes, at + cLayout.Field("laneIndexMap").Offset + 4, connection.LaneIndexY);
                        for (int m = 0; m < 4; m++)
                        {
                            RawBytes.Put(connectionBytes, at + cLayout.Field("carriagewayAndGroupIndexMap").Offset + 4 * m, connection.GroupMap[m]);
                        }

                        RawBytes.Put(connectionBytes, at + cLayout.Field("lanePositionMap").Offset, EntityResolver.ToFloat3(connection.PositionA));
                        RawBytes.Put(connectionBytes, at + cLayout.Field("lanePositionMap").Offset + 12, EntityResolver.ToFloat3(connection.PositionB));
                        WriteSmallInt(connectionBytes, at + cLayout.Field("method").Offset, cLayout.Field("method").Size, connection.Method);
                        connectionBytes[at + cLayout.Field("isUnsafe").Offset] = connection.IsUnsafe ? (byte)1 : (byte)0;
                    }

                    Entity data = EntityManager.CreateEntity();
                    byte[] ownerBytes = new byte[m_Owner.Stride];
                    RawBytes.Put(ownerBytes, m_Owner.Layout.Field("entity").Offset, node);
                    m_Owner.Write(data, ownerBytes);
                    m_Connections.Write(data, connectionBytes);
                    if (fakePrefab != Entity.Null)
                    {
                        EntityManager.AddComponentData(data, new PrefabRef(fakePrefab));
                    }

                    byte[] groupBytes = new byte[m_Groups.Stride];
                    StructLayoutInfo layout = m_Groups.Layout;
                    RawBytes.Put(groupBytes, layout.Field("laneIndex").Offset, group.LaneIndex);
                    RawBytes.Put(groupBytes, layout.Field("carriagewayAndGroup").Offset, group.CarriagewayX);
                    RawBytes.Put(groupBytes, layout.Field("carriagewayAndGroup").Offset + 4, group.CarriagewayY);
                    RawBytes.Put(groupBytes, layout.Field("lanePosition").Offset, EntityResolver.ToFloat3(group.LanePosition));
                    RawBytes.Put(groupBytes, layout.Field("edgeEntity").Offset, edge);
                    RawBytes.Put(groupBytes, layout.Field("modifiedConnections").Offset, data);
                    resolvedGroups.Add(groupBytes);
                }

                byte[] all = new byte[resolvedGroups.Count * m_Groups.Stride];
                for (int k = 0; k < resolvedGroups.Count; k++)
                {
                    Array.Copy(resolvedGroups[k], 0, all, k * m_Groups.Stride, m_Groups.Stride);
                }

                m_Groups.Write(node, all);
                m_Tag.Write(node, null);

                // Remember what this junction now looks like from here, so the next pass does not echo it back.
                string ignored;
                LaneConnectionsCommand mine = Describe(node, out ignored);
                if (mine != null)
                {
                    m_Last[node] = mine.ToBytes();
                    m_LastTarget[node] = mine.Node;
                }
            }

            m_Quiet[node] = QuietPasses;
            if (!EntityManager.HasComponent<Updated>(node))
            {
                EntityManager.AddComponent<Updated>(node);
            }

            return null;
        }

        // ------------------------------------------------------------------ helpers

        private static int ReadSmallInt(byte[] bytes, int offset, int size)
        {
            switch (size)
            {
                case 1:
                    return bytes[offset];
                case 2:
                    return RawBytes.To<ushort>(bytes, offset);
                case 8:
                    return (int)RawBytes.To<long>(bytes, offset);
                default:
                    return RawBytes.To<int>(bytes, offset);
            }
        }

        private static void WriteSmallInt(byte[] bytes, int offset, int size, int value)
        {
            switch (size)
            {
                case 1:
                    bytes[offset] = (byte)value;
                    break;
                case 2:
                    RawBytes.Put(bytes, offset, (ushort)value);
                    break;
                case 8:
                    RawBytes.Put(bytes, offset, (long)value);
                    break;
                default:
                    RawBytes.Put(bytes, offset, value);
                    break;
            }
        }

        private void Forget()
        {
            m_Primed = false;
            m_Last.Clear();
            m_LastTarget.Clear();
            m_Quiet.Clear();
        }

        private bool IsQuiet(Entity node)
        {
            int left;
            return m_Quiet.TryGetValue(node, out left) && left > 0;
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
    }
}
