using System;
using Colossal.Mathematics;
using Game.Areas;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Multiplayer.Core.Build;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using AreaNode = Game.Areas.Node;
using NetNode = Game.Net.Node;
using ObjectTransform = Game.Objects.Transform;
using RoutePosition = Game.Routes.Position;
using RouteWaypoint = Game.Routes.Waypoint;

namespace Multiplayer.Sync
{
    /// <summary>
    /// Translates between this game's entity ids and the cross-instance references used on the wire:
    /// a prefab plus a position. Two players load the same save, so a node, edge or building at a given
    /// spot is the same thing on every PC even though its entity index is not.
    /// </summary>
    internal sealed class EntityResolver
    {
        // Matching is done on the map plane: two games agree on where things are, but heights are re-sampled
        // from terrain when things are built, so y can differ by metres. A loose height check still keeps an
        // overpass apart from the road below it.
        private const float NodeTolerance = 1.5f;
        private const float EdgeTolerance = 2.5f;
        private const float ObjectTolerance = 2f;
        private const float AreaTolerance = 4f;
        private const float BlockTolerance = 6f;
        private const float WaypointTolerance = 2.5f;
        // Height only breaks ties (an overpass above a road): the game re-samples terrain heights when it
        // builds, and a save loaded on two PCs can legitimately differ by many metres on slopes.
        private const float HeightTolerance = 100f;
        private const float HeightWeight = 0.02f;
        private const int EdgeSamples = 24;

        private readonly EntityManager _entities;
        private readonly PrefabSystem _prefabs;
        private readonly EntityQuery _nodes;
        private readonly EntityQuery _edges;
        private readonly EntityQuery _objects;
        private readonly EntityQuery _areas;
        private readonly EntityQuery _blocks;
        private readonly EntityQuery _routes;
        private readonly EntityQuery _waypoints;

        public EntityResolver(World world, PrefabSystem prefabs)
        {
            _entities = world.EntityManager;
            _prefabs = prefabs;
            _nodes = _entities.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<NetNode>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            _edges = _entities.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            _objects = _entities.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<ObjectTransform>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            _areas = _entities.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Area>(), ComponentType.ReadOnly<AreaNode>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            _blocks = _entities.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Zones.Block>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            _routes = _entities.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Routes.Route>(), ComponentType.ReadOnly<Game.Routes.RouteNumber>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            _waypoints = _entities.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<RouteWaypoint>(), ComponentType.ReadOnly<RoutePosition>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
        }

        // ------------------------------------------------------------------ prefabs

        public PrefabKey DescribePrefab(Entity prefabEntity)
        {
            var key = new PrefabKey();
            if (prefabEntity == Entity.Null || !_entities.Exists(prefabEntity))
            {
                return key;
            }

            try
            {
                PrefabBase prefab = _prefabs.GetPrefab<PrefabBase>(prefabEntity);
                if (prefab != null)
                {
                    key.Type = prefab.GetType().Name;
                    key.Name = prefab.name;
                }
            }
            catch (Exception)
            {
                // Not a prefab entity; leave the key empty.
            }

            return key;
        }

        public Entity ResolvePrefab(PrefabKey key)
        {
            if (key == null || key.IsEmpty)
            {
                return Entity.Null;
            }

            if (_prefabs.TryGetPrefab(new PrefabID(key.Type, key.Name), out PrefabBase prefab) && _prefabs.TryGetEntity(prefab, out Entity entity))
            {
                return entity;
            }

            return Entity.Null;
        }

        // ------------------------------------------------------------------ describe

        public EntityRef Describe(Entity entity)
        {
            if (entity == Entity.Null)
            {
                return new EntityRef();
            }

            if (!_entities.Exists(entity))
            {
                return new EntityRef { Kind = EntityKind.Unresolvable };
            }

            var reference = new EntityRef();
            if (_entities.HasComponent<PrefabRef>(entity))
            {
                reference.Prefab = DescribePrefab(_entities.GetComponentData<PrefabRef>(entity).m_Prefab);
            }

            if (_entities.HasComponent<NetNode>(entity))
            {
                reference.Kind = EntityKind.NetNode;
                reference.Position = ToVec(_entities.GetComponentData<NetNode>(entity).m_Position);
                return reference;
            }

            if (_entities.HasComponent<Edge>(entity) && _entities.HasComponent<Curve>(entity))
            {
                Bezier4x3 curve = _entities.GetComponentData<Curve>(entity).m_Bezier;
                reference.Kind = EntityKind.NetEdge;
                reference.Position = ToVec(MathUtils.Position(curve, 0.5f));
                reference.Aux = ToVec(curve.a);
                return reference;
            }

            if (_entities.HasComponent<ObjectTransform>(entity))
            {
                reference.Kind = EntityKind.Object;
                reference.Position = ToVec(_entities.GetComponentData<ObjectTransform>(entity).m_Position);
                return reference;
            }

            if (_entities.HasComponent<Area>(entity) && _entities.HasBuffer<AreaNode>(entity))
            {
                reference.Kind = EntityKind.Area;
                reference.Position = ToVec(Centroid(_entities.GetBuffer<AreaNode>(entity)));
                return reference;
            }

            if (_entities.HasComponent<Game.Zones.Block>(entity))
            {
                Game.Zones.Block block = _entities.GetComponentData<Game.Zones.Block>(entity);
                reference.Kind = EntityKind.ZoneBlock;
                reference.Position = ToVec(block.m_Position);
                reference.Aux = new Vec3(block.m_Direction.x, 0f, block.m_Direction.y);
                return reference;
            }

            if (_entities.HasComponent<Game.Routes.Route>(entity) && _entities.HasComponent<Game.Routes.RouteNumber>(entity))
            {
                reference.Kind = EntityKind.Route;
                reference.Aux = new Vec3(_entities.GetComponentData<Game.Routes.RouteNumber>(entity).m_Number, 0f, 0f);
                return reference;
            }

            if (_entities.HasComponent<RouteWaypoint>(entity) && _entities.HasComponent<RoutePosition>(entity))
            {
                reference.Kind = EntityKind.Waypoint;
                reference.Position = ToVec(_entities.GetComponentData<RoutePosition>(entity).m_Position);
                return reference;
            }

            reference.Kind = EntityKind.Unresolvable;
            return reference;
        }

        // ------------------------------------------------------------------ resolve

        /// <summary>Entity.Null when the reference is None or nothing matches; <paramref name="failure"/> says why.</summary>
        public Entity Resolve(EntityRef reference, out string failure)
        {
            failure = null;
            if (reference == null || reference.Kind == EntityKind.None)
            {
                return Entity.Null;
            }

            if (reference.Kind == EntityKind.Unresolvable)
            {
                failure = "reference to an entity of a kind the sender could not describe";
                return Entity.Null;
            }

            Entity prefab = ResolvePrefab(reference.Prefab);
            float3 target = ToFloat3(reference.Position);
            Entity best = Entity.Null;
            float bestScore = float.MaxValue;

            switch (reference.Kind)
            {
                case EntityKind.NetNode:
                {
                    using (NativeArray<Entity> entities = _nodes.ToEntityArray(Allocator.Temp))
                    using (NativeArray<NetNode> nodes = _nodes.ToComponentDataArray<NetNode>(Allocator.Temp))
                    using (NativeArray<PrefabRef> prefabs = _nodes.ToComponentDataArray<PrefabRef>(Allocator.Temp))
                    {
                        for (int i = 0; i < entities.Length; i++)
                        {
                            float distance = PlaneDistance(nodes[i].m_Position, target);
                            if (distance > NodeTolerance)
                            {
                                continue;
                            }

                            float score = distance + HeightPenalty(nodes[i].m_Position, target) + (prefab != Entity.Null && prefabs[i].m_Prefab != prefab ? NodeTolerance : 0f);
                            if (score < bestScore)
                            {
                                bestScore = score;
                                best = entities[i];
                            }
                        }
                    }

                    break;
                }

                case EntityKind.NetEdge:
                {
                    float3 aux = ToFloat3(reference.Aux);
                    using (NativeArray<Entity> entities = _edges.ToEntityArray(Allocator.Temp))
                    using (NativeArray<Curve> curves = _edges.ToComponentDataArray<Curve>(Allocator.Temp))
                    using (NativeArray<PrefabRef> prefabs = _edges.ToComponentDataArray<PrefabRef>(Allocator.Temp))
                    {
                        for (int i = 0; i < entities.Length; i++)
                        {
                            Bezier4x3 curve = curves[i].m_Bezier;
                            // Cheap reject on the map-plane bounding box before walking the curve.
                            float2 min = math.min(math.min(curve.a.xz, curve.b.xz), math.min(curve.c.xz, curve.d.xz)) - EdgeTolerance;
                            float2 max = math.max(math.max(curve.a.xz, curve.b.xz), math.max(curve.c.xz, curve.d.xz)) + EdgeTolerance;
                            if (math.any(target.xz < min) || math.any(target.xz > max))
                            {
                                continue;
                            }

                            float distance = PlaneDistanceToCurve(curve, target);
                            if (distance > EdgeTolerance)
                            {
                                continue;
                            }

                            float score = distance + math.min(math.distance(curve.a.xz, aux.xz), math.distance(curve.d.xz, aux.xz)) * 0.1f
                                + HeightPenalty(MathUtils.Position(curve, 0.5f), target)
                                + (prefab != Entity.Null && prefabs[i].m_Prefab != prefab ? EdgeTolerance : 0f);
                            if (score < bestScore)
                            {
                                bestScore = score;
                                best = entities[i];
                            }
                        }
                    }

                    break;
                }

                case EntityKind.Object:
                {
                    using (NativeArray<Entity> entities = _objects.ToEntityArray(Allocator.Temp))
                    using (NativeArray<ObjectTransform> transforms = _objects.ToComponentDataArray<ObjectTransform>(Allocator.Temp))
                    using (NativeArray<PrefabRef> prefabs = _objects.ToComponentDataArray<PrefabRef>(Allocator.Temp))
                    {
                        for (int i = 0; i < entities.Length; i++)
                        {
                            float distance = PlaneDistance(transforms[i].m_Position, target);
                            if (distance > ObjectTolerance)
                            {
                                continue;
                            }

                            float score = distance + HeightPenalty(transforms[i].m_Position, target) + (prefab != Entity.Null && prefabs[i].m_Prefab != prefab ? ObjectTolerance : 0f);
                            if (score < bestScore)
                            {
                                bestScore = score;
                                best = entities[i];
                            }
                        }
                    }

                    break;
                }

                case EntityKind.ZoneBlock:
                {
                    float2 direction = new float2(reference.Aux.X, reference.Aux.Z);
                    using (NativeArray<Entity> entities = _blocks.ToEntityArray(Allocator.Temp))
                    using (NativeArray<Game.Zones.Block> blocks = _blocks.ToComponentDataArray<Game.Zones.Block>(Allocator.Temp))
                    {
                        for (int i = 0; i < entities.Length; i++)
                        {
                            // Blocks are 8 m cells laid out from the road's start; a slightly different split leaves the
                            // nearest block a few metres off, and the flood fill covers the same ground from there.
                            float distance = PlaneDistance(blocks[i].m_Position, target);
                            if (distance > BlockTolerance)
                            {
                                continue;
                            }

                            float score = distance + HeightPenalty(blocks[i].m_Position, target) + math.distance(blocks[i].m_Direction, direction);
                            if (score < bestScore)
                            {
                                bestScore = score;
                                best = entities[i];
                            }
                        }
                    }

                    break;
                }

                case EntityKind.Area:
                {
                    using (NativeArray<Entity> entities = _areas.ToEntityArray(Allocator.Temp))
                    {
                        for (int i = 0; i < entities.Length; i++)
                        {
                            float distance = PlaneDistance(Centroid(_entities.GetBuffer<AreaNode>(entities[i], true)), target);
                            if (distance > AreaTolerance)
                            {
                                continue;
                            }

                            float score = distance + (prefab != Entity.Null && _entities.GetComponentData<PrefabRef>(entities[i]).m_Prefab != prefab ? AreaTolerance : 0f);
                            if (score < bestScore)
                            {
                                bestScore = score;
                                best = entities[i];
                            }
                        }
                    }

                    break;
                }

                case EntityKind.Route:
                {
                    int number = (int)reference.Aux.X;
                    using (NativeArray<Entity> entities = _routes.ToEntityArray(Allocator.Temp))
                    using (NativeArray<Game.Routes.RouteNumber> numbers = _routes.ToComponentDataArray<Game.Routes.RouteNumber>(Allocator.Temp))
                    using (NativeArray<PrefabRef> prefabs = _routes.ToComponentDataArray<PrefabRef>(Allocator.Temp))
                    {
                        for (int i = 0; i < entities.Length; i++)
                        {
                            if (numbers[i].m_Number != number || (prefab != Entity.Null && prefabs[i].m_Prefab != prefab))
                            {
                                continue;
                            }

                            best = entities[i];
                            break;
                        }
                    }

                    if (best == Entity.Null)
                    {
                        failure = "no " + reference.Prefab + " line number " + number;
                        return Entity.Null;
                    }

                    break;
                }

                case EntityKind.Waypoint:
                {
                    using (NativeArray<Entity> entities = _waypoints.ToEntityArray(Allocator.Temp))
                    using (NativeArray<RoutePosition> positions = _waypoints.ToComponentDataArray<RoutePosition>(Allocator.Temp))
                    {
                        for (int i = 0; i < entities.Length; i++)
                        {
                            float distance = PlaneDistance(positions[i].m_Position, target);
                            if (distance > WaypointTolerance)
                            {
                                continue;
                            }

                            float score = distance + HeightPenalty(positions[i].m_Position, target);
                            if (score < bestScore)
                            {
                                bestScore = score;
                                best = entities[i];
                            }
                        }
                    }

                    break;
                }
            }

            if (best == Entity.Null)
            {
                failure = "no " + reference.Kind + " '" + reference.Prefab + "' near " + reference.Position;
            }

            return best;
        }

        // ------------------------------------------------------------------ geometry

        /// <summary>Distance on the map plane; infinite when heights disagree beyond any plausible terrain difference.</summary>
        private static float PlaneDistance(float3 a, float3 b)
        {
            if (math.abs(a.y - b.y) > HeightTolerance)
            {
                return float.MaxValue;
            }

            return math.distance(a.xz, b.xz);
        }

        /// <summary>Small extra used only to order candidates that both pass the plane tolerance.</summary>
        private static float HeightPenalty(float3 a, float3 b)
        {
            return math.abs(a.y - b.y) * HeightWeight;
        }

        private static float PlaneDistanceToCurve(Bezier4x3 curve, float3 target)
        {
            float best = float.MaxValue;
            for (int i = 0; i <= EdgeSamples; i++)
            {
                float3 point = MathUtils.Position(curve, i / (float)EdgeSamples);
                float distance = PlaneDistance(point, target);
                if (distance < best)
                {
                    best = distance;
                }
            }

            return best;
        }

        // ------------------------------------------------------------------ conversions

        public static Vec3 ToVec(float3 value)
        {
            return new Vec3(value.x, value.y, value.z);
        }

        public static float3 ToFloat3(Vec3 value)
        {
            return new float3(value.X, value.Y, value.Z);
        }

        public static Quat ToQuat(quaternion value)
        {
            return new Quat(value.value.x, value.value.y, value.value.z, value.value.w);
        }

        public static quaternion ToQuaternion(Quat value)
        {
            return new quaternion(value.X, value.Y, value.Z, value.W);
        }

        private static float3 Centroid(DynamicBuffer<AreaNode> nodes)
        {
            if (nodes.Length == 0)
            {
                return float3.zero;
            }

            float3 sum = float3.zero;
            for (int i = 0; i < nodes.Length; i++)
            {
                sum += nodes[i].m_Position;
            }

            return sum / nodes.Length;
        }
    }
}
