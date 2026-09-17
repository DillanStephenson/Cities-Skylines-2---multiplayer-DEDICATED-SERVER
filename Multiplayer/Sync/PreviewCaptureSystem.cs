using Colossal.Mathematics;
using Game;
using Game.Areas;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Tools;
using Multiplayer.Core.Build;
using Multiplayer.Core.Session;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using AreaNode = Game.Areas.Node;
using ObjectTransform = Game.Objects.Transform;

namespace Multiplayer.Sync
{
    /// <summary>
    /// Sends what the local player's tool is previewing: the temp entities the game shows as a ghost while a road
    /// is dragged out, a building hovers, or an area is drawn. Geometry only, a few times a second while it
    /// changes and once a second while it stays, and one empty message when it goes. The others draw it in this
    /// player's colour. Nothing is sent while the replay system is showing another player's build here.
    /// </summary>
    public partial class PreviewCaptureSystem : GameSystemBase
    {
        private const int PassInterval = 10;
        private const long RepeatMs = 1000;
        private const float DefaultRoadWidth = 8f;
        private const float DefaultObjectRadius = 6f;

        private EntityQuery m_TempEdges;
        private EntityQuery m_TempObjects;
        private EntityQuery m_TempAreas;
        private BuildReplaySystem m_Replay;
        private int m_Frame;
        private byte[] m_LastSent;
        private long m_LastSentMs;
        private int m_Sent;

        public int SentCount => m_Sent;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Replay = World.GetOrCreateSystemManaged<BuildReplaySystem>();
            m_TempEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            m_TempObjects = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<ObjectTransform>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Owner>() },
            });
            m_TempAreas = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Area>(), ComponentType.ReadOnly<AreaNode>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            GameManager manager = GameManager.instance;
            if (service == null || service.Session.State != SessionState.Connected || manager == null || manager.gameMode != GameMode.Game || manager.isGameLoading)
            {
                m_LastSent = null;
                return;
            }

            if (++m_Frame % PassInterval != 0 || m_Replay.IsApplying)
            {
                return;
            }

            PreviewCommand preview = Collect();
            byte[] bytes = preview.ToBytes();
            bool changed = m_LastSent == null ? !preview.IsEmpty : !RawBytes.Same(m_LastSent, bytes);
            bool repeat = !preview.IsEmpty && service.NowMs - m_LastSentMs >= RepeatMs;
            if (!changed && !repeat)
            {
                return;
            }

            m_LastSent = bytes;
            m_LastSentMs = service.NowMs;
            m_Sent++;
            service.SendPreview(preview);
        }

        private PreviewCommand Collect()
        {
            var preview = new PreviewCommand();
            using (NativeArray<Entity> edges = m_TempEdges.ToEntityArray(Allocator.Temp))
            using (NativeArray<Curve> curves = m_TempEdges.ToComponentDataArray<Curve>(Allocator.Temp))
            using (NativeArray<Temp> temps = m_TempEdges.ToComponentDataArray<Temp>(Allocator.Temp))
            {
                for (int i = 0; i < edges.Length && preview.Curves.Count < PreviewCommand.MaxItems; i++)
                {
                    if ((temps[i].m_Flags & TempFlags.Hidden) != 0)
                    {
                        continue;
                    }

                    Bezier4x3 bezier = curves[i].m_Bezier;
                    float width = DefaultRoadWidth;
                    if (EntityManager.HasComponent<EdgeGeometry>(edges[i]))
                    {
                        EdgeGeometry geometry = EntityManager.GetComponentData<EdgeGeometry>(edges[i]);
                        width = math.max(2f, math.distance(geometry.m_Start.m_Left.a, geometry.m_Start.m_Right.a));
                    }

                    preview.Curves.Add(new PreviewCurve
                    {
                        A = EntityResolver.ToVec(bezier.a),
                        B = EntityResolver.ToVec(bezier.b),
                        C = EntityResolver.ToVec(bezier.c),
                        D = EntityResolver.ToVec(bezier.d),
                        Width = width,
                        Deleting = (temps[i].m_Flags & TempFlags.Delete) != 0,
                    });
                }
            }

            using (NativeArray<Entity> objects = m_TempObjects.ToEntityArray(Allocator.Temp))
            using (NativeArray<ObjectTransform> transforms = m_TempObjects.ToComponentDataArray<ObjectTransform>(Allocator.Temp))
            using (NativeArray<PrefabRef> prefabs = m_TempObjects.ToComponentDataArray<PrefabRef>(Allocator.Temp))
            using (NativeArray<Temp> temps = m_TempObjects.ToComponentDataArray<Temp>(Allocator.Temp))
            {
                for (int i = 0; i < objects.Length && preview.Points.Count < PreviewCommand.MaxItems; i++)
                {
                    if ((temps[i].m_Flags & TempFlags.Hidden) != 0)
                    {
                        continue;
                    }

                    float radius = DefaultObjectRadius;
                    if (EntityManager.HasComponent<ObjectGeometryData>(prefabs[i].m_Prefab))
                    {
                        float3 size = EntityManager.GetComponentData<ObjectGeometryData>(prefabs[i].m_Prefab).m_Size;
                        radius = math.max(2f, math.max(size.x, size.z) * 0.5f);
                    }

                    preview.Points.Add(new PreviewPoint
                    {
                        Position = EntityResolver.ToVec(transforms[i].m_Position),
                        Radius = radius,
                        Deleting = (temps[i].m_Flags & TempFlags.Delete) != 0,
                    });
                }
            }

            using (NativeArray<Entity> areas = m_TempAreas.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < areas.Length && preview.Loops.Count < 100; i++)
                {
                    DynamicBuffer<AreaNode> nodes = EntityManager.GetBuffer<AreaNode>(areas[i], true);
                    if (nodes.Length < 2)
                    {
                        continue;
                    }

                    var loop = new PreviewLoop();
                    for (int n = 0; n < nodes.Length && n < 400; n++)
                    {
                        loop.Nodes.Add(EntityResolver.ToVec(nodes[n].m_Position));
                    }

                    preview.Loops.Add(loop);
                }
            }

            return preview;
        }
    }
}
