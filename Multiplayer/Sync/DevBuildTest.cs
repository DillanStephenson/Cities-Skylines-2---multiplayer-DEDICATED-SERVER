using System;
using Colossal.Logging;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Zones;
using Multiplayer.Core.Build;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using NetNode = Game.Net.Node;
using ObjectTransform = Game.Objects.Transform;

namespace Multiplayer.Sync
{
    /// <summary>
    /// Makes build commands the way the tools would (a road, a tree, a zoning fill, a bulldoze) on open
    /// ground near the city, so the whole capture/relay/replay chain can be exercised from one PC without
    /// clicking. Also the counting helpers used to check the result.
    /// </summary>
    internal static class DevBuildTest
    {
        private static readonly string[] RoadCandidates = { "Small Road", "Two-Lane Road", "Gravel Road", "Alley" };
        public const float RoadLength = 96f;

        // ------------------------------------------------------------------ road

        public static BuildCommand MakeStraightRoad(World world, ILog log, out float3 start)
        {
            start = float3.zero;
            var prefabSystem = world.GetOrCreateSystemManaged<PrefabSystem>();
            var resolver = new EntityResolver(world, prefabSystem);

            Entity prefabEntity = Entity.Null;
            string chosen = null;
            foreach (string candidate in RoadCandidates)
            {
                prefabEntity = resolver.ResolvePrefab(new PrefabKey { Type = "RoadPrefab", Name = candidate });
                if (prefabEntity != Entity.Null)
                {
                    chosen = candidate;
                    break;
                }
            }

            if (prefabEntity == Entity.Null)
            {
                log.Warn("Dev build: none of the candidate road prefabs exist (" + string.Join(", ", RoadCandidates) + ")");
                return null;
            }

            EntityManager entities = world.EntityManager;
            EntityQuery nodeQuery = Query<NetNode>(entities);
            EntityQuery objectQuery = Query<ObjectTransform>(entities);
            NativeArray<NetNode> nodes = nodeQuery.ToComponentDataArray<NetNode>(Allocator.Temp);
            NativeArray<ObjectTransform> objects = objectQuery.ToComponentDataArray<ObjectTransform>(Allocator.Temp);
            try
            {
                if (nodes.Length == 0)
                {
                    log.Warn("Dev build: the city has no road nodes to anchor to");
                    return null;
                }

                var terrain = world.GetOrCreateSystemManaged<TerrainSystem>();
                TerrainHeightData heights = terrain.GetHeightData();
                float3 anchor = nodes[nodes.Length / 2].m_Position;
                float3? found = null;
                float2[] directions = { new float2(1, 0), new float2(-1, 0), new float2(0, 1), new float2(0, -1), new float2(1, 1), new float2(-1, -1), new float2(1, -1), new float2(-1, 1) };
                for (int ring = 1; ring <= 12 && found == null; ring++)
                {
                    float radius = 120f * ring;
                    foreach (float2 direction in directions)
                    {
                        float3 candidate = anchor + new float3(direction.x * radius, 0f, direction.y * radius);
                        float3 echoCandidate = candidate + new float3(150f, 0f, 0f);
                        // Room for the road and for an echoed copy 150 m further along X, on ground flat enough
                        // that the echo (which keeps the original heights) is still a valid road.
                        if (IsOpen(candidate, nodes, objects) && IsOpen(echoCandidate, nodes, objects)
                            && IsFlat(ref heights, candidate) && IsFlat(ref heights, echoCandidate)
                            && math.abs(TerrainUtils.SampleHeight(ref heights, candidate) - TerrainUtils.SampleHeight(ref heights, echoCandidate)) < 6f)
                        {
                            found = candidate;
                            break;
                        }
                    }
                }

                if (found == null)
                {
                    log.Warn("Dev build: no open, flat ground found near " + anchor);
                    return null;
                }

                float3 a = found.Value;
                float3 d = a + new float3(RoadLength, 0f, 0f);
                a.y = TerrainUtils.SampleHeight(ref heights, a);
                d.y = TerrainUtils.SampleHeight(ref heights, d);
                float3 b = math.lerp(a, d, 1f / 3f);
                float3 c = math.lerp(a, d, 2f / 3f);
                quaternion rotation = quaternion.LookRotationSafe(math.normalize(d - a), math.up());
                start = a;

                var command = new BuildCommand
                {
                    ToolId = "Dev Test road",
                    Definitions =
                    {
                        new DefinitionData
                        {
                            Prefab = resolver.DescribePrefab(prefabEntity),
                            RandomSeed = new System.Random().Next(),
                            Course = new NetCourseData
                            {
                                Start = new CoursePosData { Position = EntityResolver.ToVec(a), Rotation = EntityResolver.ToQuat(rotation), CourseDelta = 0f, Flags = (uint)CoursePosFlags.IsFirst, ParentMesh = -1 },
                                End = new CoursePosData { Position = EntityResolver.ToVec(d), Rotation = EntityResolver.ToQuat(rotation), CourseDelta = 1f, Flags = (uint)CoursePosFlags.IsLast, ParentMesh = -1 },
                                A = EntityResolver.ToVec(a),
                                B = EntityResolver.ToVec(b),
                                C = EntityResolver.ToVec(c),
                                D = EntityResolver.ToVec(d),
                                Length = math.distance(a, d),
                                FixedIndex = -1,
                            },
                        },
                    },
                };

                log.Info("Dev build: " + chosen + " from " + a + " to " + d);
                return command;
            }
            finally
            {
                nodes.Dispose();
                objects.Dispose();
                nodeQuery.Dispose();
                objectQuery.Dispose();
            }
        }

        // ------------------------------------------------------------------ tree

        public static BuildCommand MakeTree(World world, ILog log, float3 position)
        {
            var prefabSystem = world.GetOrCreateSystemManaged<PrefabSystem>();
            var resolver = new EntityResolver(world, prefabSystem);
            EntityManager entities = world.EntityManager;

            EntityQuery treePrefabs = entities.CreateEntityQuery(ComponentType.ReadOnly<TreeData>(), ComponentType.ReadOnly<PrefabData>());
            Entity treePrefab = Entity.Null;
            using (NativeArray<Entity> candidates = treePrefabs.ToEntityArray(Allocator.Temp))
            {
                if (candidates.Length > 0)
                {
                    treePrefab = candidates[0];
                }
            }

            treePrefabs.Dispose();
            if (treePrefab == Entity.Null)
            {
                log.Warn("Dev build: no tree prefab found");
                return null;
            }

            var terrain = world.GetOrCreateSystemManaged<TerrainSystem>();
            TerrainHeightData heights = terrain.GetHeightData();
            position.y = TerrainUtils.SampleHeight(ref heights, position);
            PrefabKey key = resolver.DescribePrefab(treePrefab);
            log.Info("Dev build: tree " + key + " at " + position);

            return new BuildCommand
            {
                ToolId = "Dev Test tree",
                Definitions =
                {
                    new DefinitionData
                    {
                        Prefab = key,
                        RandomSeed = new System.Random().Next(),
                        Object = new ObjectDefinitionData
                        {
                            Position = EntityResolver.ToVec(position),
                            Rotation = Quat.Identity,
                            LocalRotation = Quat.Identity,
                            Scale = new Vec3(1f, 1f, 1f),
                            Intensity = 1f,
                            Age = 0.5f,
                            Probability = 100,
                            PrefabSubIndex = -1,
                            ParentMesh = -1,
                            GroupIndex = -1,
                        },
                    },
                },
            };
        }

        // ------------------------------------------------------------------ zoning

        /// <summary>Flood-fill the first residential zone onto the zoning block nearest <paramref name="near"/>.</summary>
        public static BuildCommand MakeZoneFill(World world, ILog log, float3 near)
        {
            var prefabSystem = world.GetOrCreateSystemManaged<PrefabSystem>();
            var resolver = new EntityResolver(world, prefabSystem);
            EntityManager entities = world.EntityManager;

            Entity zonePrefab = Entity.Null;
            EntityQuery zonePrefabs = entities.CreateEntityQuery(ComponentType.ReadOnly<ZoneData>(), ComponentType.ReadOnly<PrefabData>());
            using (NativeArray<Entity> candidates = zonePrefabs.ToEntityArray(Allocator.Temp))
            using (NativeArray<ZoneData> zones = zonePrefabs.ToComponentDataArray<ZoneData>(Allocator.Temp))
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (zones[i].m_AreaType == AreaType.Residential)
                    {
                        zonePrefab = candidates[i];
                        break;
                    }
                }
            }

            zonePrefabs.Dispose();
            if (zonePrefab == Entity.Null)
            {
                log.Warn("Dev build: no residential zone prefab found");
                return null;
            }

            Entity block = Entity.Null;
            float best = 60f;
            float3 blockPosition = float3.zero;
            EntityQuery blocks = Query<Block>(entities);
            using (NativeArray<Entity> blockEntities = blocks.ToEntityArray(Allocator.Temp))
            using (NativeArray<Block> blockData = blocks.ToComponentDataArray<Block>(Allocator.Temp))
            {
                for (int i = 0; i < blockEntities.Length; i++)
                {
                    float distance = math.distance(blockData[i].m_Position.xz, near.xz);
                    if (distance < best)
                    {
                        best = distance;
                        block = blockEntities[i];
                        blockPosition = blockData[i].m_Position;
                    }
                }
            }

            blocks.Dispose();
            if (block == Entity.Null)
            {
                log.Warn("Dev build: no zoning block within 60 m of " + near);
                return null;
            }

            PrefabKey key = resolver.DescribePrefab(zonePrefab);
            log.Info("Dev build: zone " + key + " flood fill at block " + blockPosition);
            return new BuildCommand
            {
                ToolId = "Dev Test zone",
                Definitions =
                {
                    new DefinitionData
                    {
                        Prefab = key,
                        Original = resolver.Describe(block),
                        Zoning = new ZoningData
                        {
                            A = EntityResolver.ToVec(blockPosition),
                            B = EntityResolver.ToVec(blockPosition),
                            C = EntityResolver.ToVec(blockPosition),
                            D = EntityResolver.ToVec(blockPosition),
                            Flags = (uint)(ZoningFlags.FloodFill | ZoningFlags.Zone),
                        },
                    },
                },
            };
        }

        // ------------------------------------------------------------------ bulldoze

        /// <summary>Delete the road edge whose curve passes nearest <paramref name="near"/>.</summary>
        public static BuildCommand MakeBulldozeEdge(World world, ILog log, float3 near)
        {
            var prefabSystem = world.GetOrCreateSystemManaged<PrefabSystem>();
            var resolver = new EntityResolver(world, prefabSystem);
            EntityManager entities = world.EntityManager;

            Entity edge = Entity.Null;
            float best = 30f;
            Curve bestCurve = default;
            EntityQuery edges = entities.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            using (NativeArray<Entity> edgeEntities = edges.ToEntityArray(Allocator.Temp))
            using (NativeArray<Curve> curves = edges.ToComponentDataArray<Curve>(Allocator.Temp))
            {
                for (int i = 0; i < edgeEntities.Length; i++)
                {
                    float distance = MathUtils.Distance(curves[i].m_Bezier, near, out _);
                    if (distance < best)
                    {
                        best = distance;
                        edge = edgeEntities[i];
                        bestCurve = curves[i];
                    }
                }
            }

            edges.Dispose();
            if (edge == Entity.Null)
            {
                log.Warn("Dev build: no road edge within 30 m of " + near);
                return null;
            }

            Edge ends = entities.GetComponentData<Edge>(edge);
            Bezier4x3 curve = bestCurve.m_Bezier;
            quaternion startRotation = quaternion.LookRotationSafe(math.normalizesafe(MathUtils.StartTangent(curve)), math.up());
            quaternion endRotation = quaternion.LookRotationSafe(math.normalizesafe(MathUtils.EndTangent(curve)), math.up());
            float2 elevation = entities.HasComponent<Elevation>(edge) ? entities.GetComponentData<Elevation>(edge).m_Elevation : float2.zero;
            EntityRef target = resolver.Describe(edge);
            log.Info("Dev build: bulldoze edge " + target);

            return new BuildCommand
            {
                ToolId = "Dev Test bulldoze",
                Definitions =
                {
                    new DefinitionData
                    {
                        Original = target,
                        Flags = (uint)CreationFlags.Delete,
                        Course = new NetCourseData
                        {
                            Start = new CoursePosData { Entity = resolver.Describe(ends.m_Start), Position = EntityResolver.ToVec(curve.a), Rotation = EntityResolver.ToQuat(startRotation), ElevationX = elevation.x, ElevationY = elevation.x, CourseDelta = 0f, Flags = (uint)CoursePosFlags.IsFirst, ParentMesh = -1 },
                            End = new CoursePosData { Entity = resolver.Describe(ends.m_End), Position = EntityResolver.ToVec(curve.d), Rotation = EntityResolver.ToQuat(endRotation), ElevationX = elevation.y, ElevationY = elevation.y, CourseDelta = 1f, Flags = (uint)CoursePosFlags.IsLast, ParentMesh = -1 },
                            A = EntityResolver.ToVec(curve.a),
                            B = EntityResolver.ToVec(curve.b),
                            C = EntityResolver.ToVec(curve.c),
                            D = EntityResolver.ToVec(curve.d),
                            ElevationX = elevation.x,
                            ElevationY = elevation.y,
                            Length = bestCurve.m_Length,
                            FixedIndex = -1,
                        },
                    },
                },
            };
        }

        // ------------------------------------------------------------------ checks

        public static int CountNodesNear(World world, float3 position, float radius)
        {
            EntityQuery query = Query<NetNode>(world.EntityManager);
            int count = 0;
            using (NativeArray<NetNode> nodes = query.ToComponentDataArray<NetNode>(Allocator.Temp))
            {
                for (int i = 0; i < nodes.Length; i++)
                {
                    if (math.distance(nodes[i].m_Position.xz, position.xz) <= radius)
                    {
                        count++;
                    }
                }
            }

            query.Dispose();
            return count;
        }

        public static int CountObjectsNear(World world, float3 position, float radius)
        {
            EntityQuery query = Query<ObjectTransform>(world.EntityManager);
            int count = 0;
            using (NativeArray<ObjectTransform> transforms = query.ToComponentDataArray<ObjectTransform>(Allocator.Temp))
            {
                for (int i = 0; i < transforms.Length; i++)
                {
                    if (math.distance(transforms[i].m_Position.xz, position.xz) <= radius)
                    {
                        count++;
                    }
                }
            }

            query.Dispose();
            return count;
        }

        /// <summary>Cells carrying a zone type in blocks within the radius.</summary>
        public static int CountZonedCellsNear(World world, float3 position, float radius)
        {
            EntityManager entities = world.EntityManager;
            EntityQuery query = Query<Block>(entities);
            int count = 0;
            using (NativeArray<Entity> blocks = query.ToEntityArray(Allocator.Temp))
            using (NativeArray<Block> data = query.ToComponentDataArray<Block>(Allocator.Temp))
            {
                for (int i = 0; i < blocks.Length; i++)
                {
                    if (math.distance(data[i].m_Position.xz, position.xz) > radius || !entities.HasBuffer<Cell>(blocks[i]))
                    {
                        continue;
                    }

                    DynamicBuffer<Cell> cells = entities.GetBuffer<Cell>(blocks[i], true);
                    for (int j = 0; j < cells.Length; j++)
                    {
                        if (!cells[j].m_Zone.Equals(ZoneType.None))
                        {
                            count++;
                        }
                    }
                }
            }

            query.Dispose();
            return count;
        }

        // ------------------------------------------------------------------ terrain

        /// <summary>A few raise strokes at one spot, as holding the terrain tool there for a moment would send.</summary>
        public static BuildCommand MakeTerrainRaise(World world, ILog log, float3 position)
        {
            var prefabSystem = world.GetOrCreateSystemManaged<PrefabSystem>();
            var resolver = new EntityResolver(world, prefabSystem);
            EntityManager entities = world.EntityManager;

            Entity tool = Entity.Null;
            EntityQuery tools = entities.CreateEntityQuery(ComponentType.ReadOnly<TerraformingData>(), ComponentType.ReadOnly<PrefabData>());
            using (NativeArray<Entity> candidates = tools.ToEntityArray(Allocator.Temp))
            using (NativeArray<TerraformingData> data = tools.ToComponentDataArray<TerraformingData>(Allocator.Temp))
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (data[i].m_Target == TerraformingTarget.Height && data[i].m_Type == TerraformingType.Shift)
                    {
                        tool = candidates[i];
                        break;
                    }
                }
            }

            tools.Dispose();

            Entity brush = Entity.Null;
            int bestPriority = int.MaxValue;
            EntityQuery brushes = entities.CreateEntityQuery(ComponentType.ReadOnly<BrushData>(), ComponentType.ReadOnly<PrefabData>());
            using (NativeArray<Entity> candidates = brushes.ToEntityArray(Allocator.Temp))
            using (NativeArray<BrushData> data = brushes.ToComponentDataArray<BrushData>(Allocator.Temp))
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (data[i].m_Priority < bestPriority)
                    {
                        bestPriority = data[i].m_Priority;
                        brush = candidates[i];
                    }
                }
            }

            brushes.Dispose();
            if (tool == Entity.Null || brush == Entity.Null)
            {
                log.Warn("Dev build: no raise-terrain tool or brush prefab found");
                return null;
            }

            position.y = HeightAt(world, position);
            PrefabKey toolKey = resolver.DescribePrefab(tool);
            PrefabKey brushKey = resolver.DescribePrefab(brush);
            log.Info("Dev build: terrain " + toolKey + " with " + brushKey + " at " + position);

            var command = new BuildCommand { ToolId = "Dev Test terrain" };
            for (int i = 0; i < 2; i++)
            {
                command.Definitions.Add(new DefinitionData
                {
                    Prefab = brushKey,
                    Brush = new BrushDefinitionData
                    {
                        Tool = toolKey,
                        LineA = EntityResolver.ToVec(position),
                        LineB = EntityResolver.ToVec(position),
                        Target = EntityResolver.ToVec(position + new float3(0f, 10f, 0f)),
                        Start = EntityResolver.ToVec(position),
                        Angle = 0f,
                        Size = 40f,
                        Strength = 0.5f,
                        Time = 0.1f,
                    },
                });
            }

            return command;
        }

        public static float HeightAt(World world, float3 position)
        {
            var terrain = world.GetOrCreateSystemManaged<TerrainSystem>();
            TerrainHeightData heights = terrain.GetHeightData();
            return TerrainUtils.SampleHeight(ref heights, position);
        }

        // ------------------------------------------------------------------ policies

        /// <summary>The first city service building and the first policy that can switch such a building off.</summary>
        public static bool FindBuildingPolicy(World world, ILog log, out Entity building, out Entity policy)
        {
            EntityManager entities = world.EntityManager;
            building = Entity.Null;
            policy = Entity.Null;

            EntityQuery policies = entities.CreateEntityQuery(ComponentType.ReadOnly<BuildingOptionData>(), ComponentType.ReadOnly<PrefabData>());
            using (NativeArray<Entity> candidates = policies.ToEntityArray(Allocator.Temp))
            using (NativeArray<BuildingOptionData> data = policies.ToComponentDataArray<BuildingOptionData>(Allocator.Temp))
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (Game.Buildings.BuildingUtils.HasOption(data[i], Game.Buildings.BuildingOption.Inactive))
                    {
                        policy = candidates[i];
                        break;
                    }
                }
            }

            policies.Dispose();

            EntityQuery buildings = entities.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Buildings.Building>(),
                    ComponentType.ReadOnly<Game.City.CityServiceUpkeep>(),
                    ComponentType.ReadOnly<Game.Policies.Policy>(),
                    ComponentType.ReadOnly<ObjectTransform>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Game.Common.Owner>() },
            });
            using (NativeArray<Entity> candidates = buildings.ToEntityArray(Allocator.Temp))
            {
                if (candidates.Length > 0)
                {
                    building = candidates[0];
                }
            }

            buildings.Dispose();
            if (policy == Entity.Null || building == Entity.Null)
            {
                log.Warn("Dev build: switch-off policy " + (policy == Entity.Null ? "not found" : "found") + ", service building " + (building == Entity.Null ? "not found" : "found"));
                return false;
            }

            return true;
        }

        public static bool IsPolicyActive(World world, Entity target, Entity policy)
        {
            EntityManager entities = world.EntityManager;
            if (target == Entity.Null || !entities.Exists(target) || !entities.HasBuffer<Game.Policies.Policy>(target))
            {
                return false;
            }

            DynamicBuffer<Game.Policies.Policy> buffer = entities.GetBuffer<Game.Policies.Policy>(target, true);
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i].m_Policy == policy)
                {
                    return (buffer[i].m_Flags & Game.Policies.PolicyFlags.Active) != 0;
                }
            }

            return false;
        }

        public static string DescribeEntity(World world, Entity entity)
        {
            var prefabSystem = world.GetOrCreateSystemManaged<PrefabSystem>();
            return new EntityResolver(world, prefabSystem).Describe(entity).ToString();
        }

        // ------------------------------------------------------------------ helpers

        private static EntityQuery Query<T>(EntityManager entities) where T : unmanaged, IComponentData
        {
            return entities.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<T>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
        }

        /// <summary>Less than 4 m of height variation along the road span and 40 m to either side of it.</summary>
        private static bool IsFlat(ref TerrainHeightData heights, float3 start)
        {
            float min = float.MaxValue;
            float max = float.MinValue;
            for (int i = 0; i <= 6; i++)
            {
                for (int side = -1; side <= 1; side++)
                {
                    float3 sample = start + new float3(RoadLength * i / 6f, 0f, side * 40f);
                    float height = TerrainUtils.SampleHeight(ref heights, sample);
                    min = math.min(min, height);
                    max = math.max(max, height);
                }
            }

            return max - min < 4f;
        }

        private static bool IsOpen(float3 candidate, NativeArray<NetNode> nodes, NativeArray<ObjectTransform> objects)
        {
            float3 end = candidate + new float3(RoadLength, 0f, 0f);
            for (int i = 0; i < nodes.Length; i++)
            {
                if (DistanceToSegmentXZ(nodes[i].m_Position, candidate, end) < 70f)
                {
                    return false;
                }
            }

            for (int i = 0; i < objects.Length; i++)
            {
                if (DistanceToSegmentXZ(objects[i].m_Position, candidate, end) < 40f)
                {
                    return false;
                }
            }

            return true;
        }

        private static float DistanceToSegmentXZ(float3 point, float3 a, float3 b)
        {
            float2 p = point.xz;
            float2 s = a.xz;
            float2 e = b.xz;
            float2 ab = e - s;
            float t = math.saturate(math.dot(p - s, ab) / math.max(1e-4f, math.dot(ab, ab)));
            return math.distance(p, s + ab * t);
        }
    }
}
