using System;
using System.Collections.Generic;
using System.Text;
using Colossal.Mathematics;
using Game.Common;
using Game.Tools;
using Multiplayer.Core.Build;
using Unity.Entities;
using Unity.Mathematics;
using AreaNode = Game.Areas.Node;

namespace Multiplayer.Sync
{
    /// <summary>
    /// Converts the game's definition entities to wire data and back. Reading happens on the sender in the
    /// apply frame; creating happens on receivers, who then let the game's own generators build the temps.
    /// </summary>
    internal sealed class DefinitionCodec
    {
        private readonly EntityManager _entities;
        private readonly EntityResolver _resolver;

        public DefinitionCodec(EntityManager entities, EntityResolver resolver)
        {
            _entities = entities;
            _resolver = resolver;
        }

        // ------------------------------------------------------------------ read

        public DefinitionData Read(Entity definition)
        {
            CreationDefinition creation = _entities.GetComponentData<CreationDefinition>(definition);
            var data = new DefinitionData
            {
                Prefab = _resolver.DescribePrefab(creation.m_Prefab),
                SubPrefab = _resolver.DescribePrefab(creation.m_SubPrefab),
                Original = _resolver.Describe(creation.m_Original),
                Owner = _resolver.Describe(creation.m_Owner),
                Attached = _resolver.Describe(creation.m_Attached),
                Flags = (uint)creation.m_Flags,
                RandomSeed = creation.m_RandomSeed,
            };

            if (_entities.HasComponent<NetCourse>(definition))
            {
                NetCourse course = _entities.GetComponentData<NetCourse>(definition);
                data.Course = new NetCourseData
                {
                    Start = ReadCoursePos(course.m_StartPosition),
                    End = ReadCoursePos(course.m_EndPosition),
                    A = EntityResolver.ToVec(course.m_Curve.a),
                    B = EntityResolver.ToVec(course.m_Curve.b),
                    C = EntityResolver.ToVec(course.m_Curve.c),
                    D = EntityResolver.ToVec(course.m_Curve.d),
                    ElevationX = course.m_Elevation.x,
                    ElevationY = course.m_Elevation.y,
                    Length = course.m_Length,
                    FixedIndex = course.m_FixedIndex,
                };
            }

            if (_entities.HasComponent<ObjectDefinition>(definition))
            {
                ObjectDefinition obj = _entities.GetComponentData<ObjectDefinition>(definition);
                data.Object = new ObjectDefinitionData
                {
                    Position = EntityResolver.ToVec(obj.m_Position),
                    LocalPosition = EntityResolver.ToVec(obj.m_LocalPosition),
                    Scale = EntityResolver.ToVec(obj.m_Scale),
                    Rotation = EntityResolver.ToQuat(obj.m_Rotation),
                    LocalRotation = EntityResolver.ToQuat(obj.m_LocalRotation),
                    Elevation = obj.m_Elevation,
                    Intensity = obj.m_Intensity,
                    Age = obj.m_Age,
                    IsDecoration = obj.m_IsDecoration,
                    ParentMesh = obj.m_ParentMesh,
                    GroupIndex = obj.m_GroupIndex,
                    Probability = obj.m_Probability,
                    PrefabSubIndex = obj.m_PrefabSubIndex,
                };
            }

            if (_entities.HasComponent<OwnerDefinition>(definition))
            {
                OwnerDefinition owner = _entities.GetComponentData<OwnerDefinition>(definition);
                data.OwnerDefinition = new OwnerDefinitionData
                {
                    Prefab = _resolver.DescribePrefab(owner.m_Prefab),
                    Position = EntityResolver.ToVec(owner.m_Position),
                    Rotation = EntityResolver.ToQuat(owner.m_Rotation),
                };
            }

            if (_entities.HasBuffer<AreaNode>(definition))
            {
                DynamicBuffer<AreaNode> nodes = _entities.GetBuffer<AreaNode>(definition, true);
                data.AreaNodes = new List<AreaNodeData>(nodes.Length);
                for (int i = 0; i < nodes.Length; i++)
                {
                    data.AreaNodes.Add(new AreaNodeData { Position = EntityResolver.ToVec(nodes[i].m_Position), Elevation = nodes[i].m_Elevation });
                }
            }

            if (_entities.HasComponent<Zoning>(definition))
            {
                Zoning zoning = _entities.GetComponentData<Zoning>(definition);
                data.Zoning = new ZoningData
                {
                    A = EntityResolver.ToVec(zoning.m_Position.a),
                    B = EntityResolver.ToVec(zoning.m_Position.b),
                    C = EntityResolver.ToVec(zoning.m_Position.c),
                    D = EntityResolver.ToVec(zoning.m_Position.d),
                    Flags = (uint)zoning.m_Flags,
                };
            }

            if (_entities.HasComponent<BrushDefinition>(definition))
            {
                BrushDefinition brush = _entities.GetComponentData<BrushDefinition>(definition);
                data.Brush = new BrushDefinitionData
                {
                    Tool = _resolver.DescribePrefab(brush.m_Tool),
                    LineA = EntityResolver.ToVec(brush.m_Line.a),
                    LineB = EntityResolver.ToVec(brush.m_Line.b),
                    Target = EntityResolver.ToVec(brush.m_Target),
                    Start = EntityResolver.ToVec(brush.m_Start),
                    Angle = brush.m_Angle,
                    Size = brush.m_Size,
                    Strength = brush.m_Strength,
                    Time = brush.m_Time,
                };
            }

            if (_entities.HasBuffer<Game.Routes.WaypointDefinition>(definition))
            {
                DynamicBuffer<Game.Routes.WaypointDefinition> waypoints = _entities.GetBuffer<Game.Routes.WaypointDefinition>(definition, true);
                data.Waypoints = new List<WaypointData>(waypoints.Length);
                for (int i = 0; i < waypoints.Length; i++)
                {
                    data.Waypoints.Add(new WaypointData
                    {
                        Position = EntityResolver.ToVec(waypoints[i].m_Position),
                        Connection = _resolver.Describe(waypoints[i].m_Connection),
                        Original = _resolver.Describe(waypoints[i].m_Original),
                    });
                }
            }

            if (_entities.HasComponent<ColorDefinition>(definition))
            {
                UnityEngine.Color32 color = _entities.GetComponentData<ColorDefinition>(definition).m_Color;
                data.Color = new ColorData { R = color.r, G = color.g, B = color.b, A = color.a };
            }

            return data;
        }

        private CoursePosData ReadCoursePos(CoursePos pos)
        {
            return new CoursePosData
            {
                Entity = _resolver.Describe(pos.m_Entity),
                Position = EntityResolver.ToVec(pos.m_Position),
                Rotation = EntityResolver.ToQuat(pos.m_Rotation),
                ElevationX = pos.m_Elevation.x,
                ElevationY = pos.m_Elevation.y,
                CourseDelta = pos.m_CourseDelta,
                SplitPosition = pos.m_SplitPosition,
                Flags = (uint)pos.m_Flags,
                ParentMesh = pos.m_ParentMesh,
            };
        }

        // ------------------------------------------------------------------ create

        /// <summary>
        /// Recreate a definition entity, with <c>Updated</c> so the generators pick it up this frame.
        /// Returns Entity.Null when a required reference (the thing being deleted, moved or upgraded) cannot
        /// be found on this game; optional references that fail are left null and noted in <paramref name="problems"/>.
        /// </summary>
        public Entity Create(DefinitionData data, StringBuilder problems)
        {
            Entity prefab = _resolver.ResolvePrefab(data.Prefab);
            if (!data.Prefab.IsEmpty && prefab == Entity.Null)
            {
                problems.Append("prefab '").Append(data.Prefab).Append("' not found; ");
                return Entity.Null;
            }

            Entity original = ResolveOrNote(data.Original, "original", problems);
            var flags = (CreationFlags)data.Flags;
            bool needsOriginal = data.Original.Kind != EntityKind.None && (flags & (CreationFlags.Delete | CreationFlags.Relocate | CreationFlags.Upgrade | CreationFlags.Recreate | CreationFlags.Repair)) != 0;
            if (needsOriginal && original == Entity.Null)
            {
                problems.Append("skipped: cannot find the entity to change; ");
                return Entity.Null;
            }

            // A sub-part whose owner or attachment is missing here would be created loose; better not at all.
            Entity owner = ResolveOrNote(data.Owner, "owner", problems);
            if (data.Owner.Kind != EntityKind.None && owner == Entity.Null)
            {
                problems.Append("skipped: owner missing; ");
                return Entity.Null;
            }

            Entity attached = ResolveOrNote(data.Attached, "attached", problems);
            if (data.Attached.Kind != EntityKind.None && attached == Entity.Null)
            {
                problems.Append("skipped: attachment missing; ");
                return Entity.Null;
            }

            Entity brushTool = Entity.Null;
            if (data.Brush != null)
            {
                brushTool = _resolver.ResolvePrefab(data.Brush.Tool);
                if (brushTool == Entity.Null)
                {
                    problems.Append("terraforming tool '").Append(data.Brush.Tool).Append("' not found; ");
                    return Entity.Null;
                }
            }

            List<Game.Routes.WaypointDefinition> waypoints = null;
            if (data.Waypoints != null)
            {
                waypoints = new List<Game.Routes.WaypointDefinition>(data.Waypoints.Count);
                foreach (WaypointData waypoint in data.Waypoints)
                {
                    Entity connection = ResolveOrNote(waypoint.Connection, "stop", problems);
                    if (waypoint.Connection.Kind != EntityKind.None && connection == Entity.Null)
                    {
                        problems.Append("skipped: a stop of the line is missing here; ");
                        return Entity.Null;
                    }

                    Entity originalWaypoint = ResolveOrNote(waypoint.Original, "waypoint", problems);
                    if (waypoint.Original.Kind != EntityKind.None && originalWaypoint == Entity.Null)
                    {
                        problems.Append("skipped: a waypoint of the line is missing here; ");
                        return Entity.Null;
                    }

                    waypoints.Add(new Game.Routes.WaypointDefinition(EntityResolver.ToFloat3(waypoint.Position)) { m_Connection = connection, m_Original = originalWaypoint });
                }
            }

            Entity entity = _entities.CreateEntity();
            _entities.AddComponentData(entity, new CreationDefinition
            {
                m_Prefab = prefab,
                m_SubPrefab = _resolver.ResolvePrefab(data.SubPrefab),
                m_Original = original,
                m_Owner = owner,
                m_Attached = attached,
                m_Flags = flags,
                m_RandomSeed = data.RandomSeed,
            });

            if (data.Course != null)
            {
                _entities.AddComponentData(entity, new NetCourse
                {
                    m_StartPosition = CreateCoursePos(data.Course.Start, problems),
                    m_EndPosition = CreateCoursePos(data.Course.End, problems),
                    m_Curve = new Bezier4x3(
                        EntityResolver.ToFloat3(data.Course.A),
                        EntityResolver.ToFloat3(data.Course.B),
                        EntityResolver.ToFloat3(data.Course.C),
                        EntityResolver.ToFloat3(data.Course.D)),
                    m_Elevation = new float2(data.Course.ElevationX, data.Course.ElevationY),
                    m_Length = data.Course.Length,
                    m_FixedIndex = data.Course.FixedIndex,
                });
            }

            if (data.Object != null)
            {
                _entities.AddComponentData(entity, new ObjectDefinition
                {
                    m_Position = EntityResolver.ToFloat3(data.Object.Position),
                    m_LocalPosition = EntityResolver.ToFloat3(data.Object.LocalPosition),
                    m_Scale = EntityResolver.ToFloat3(data.Object.Scale),
                    m_Rotation = EntityResolver.ToQuaternion(data.Object.Rotation),
                    m_LocalRotation = EntityResolver.ToQuaternion(data.Object.LocalRotation),
                    m_Elevation = data.Object.Elevation,
                    m_Intensity = data.Object.Intensity,
                    m_Age = data.Object.Age,
                    m_IsDecoration = data.Object.IsDecoration,
                    m_ParentMesh = data.Object.ParentMesh,
                    m_GroupIndex = data.Object.GroupIndex,
                    m_Probability = data.Object.Probability,
                    m_PrefabSubIndex = data.Object.PrefabSubIndex,
                });
            }

            if (data.OwnerDefinition != null)
            {
                _entities.AddComponentData(entity, new OwnerDefinition
                {
                    m_Prefab = _resolver.ResolvePrefab(data.OwnerDefinition.Prefab),
                    m_Position = EntityResolver.ToFloat3(data.OwnerDefinition.Position),
                    m_Rotation = EntityResolver.ToQuaternion(data.OwnerDefinition.Rotation),
                });
            }

            if (data.AreaNodes != null)
            {
                DynamicBuffer<AreaNode> nodes = _entities.AddBuffer<AreaNode>(entity);
                foreach (AreaNodeData node in data.AreaNodes)
                {
                    nodes.Add(new AreaNode(EntityResolver.ToFloat3(node.Position), node.Elevation));
                }
            }

            if (data.Zoning != null)
            {
                _entities.AddComponentData(entity, new Zoning
                {
                    m_Position = new Quad3(
                        EntityResolver.ToFloat3(data.Zoning.A),
                        EntityResolver.ToFloat3(data.Zoning.B),
                        EntityResolver.ToFloat3(data.Zoning.C),
                        EntityResolver.ToFloat3(data.Zoning.D)),
                    m_Flags = (ZoningFlags)data.Zoning.Flags,
                });
            }

            if (data.Brush != null)
            {
                _entities.AddComponentData(entity, new BrushDefinition
                {
                    m_Tool = brushTool,
                    m_Line = new Line3.Segment(EntityResolver.ToFloat3(data.Brush.LineA), EntityResolver.ToFloat3(data.Brush.LineB)),
                    m_Target = EntityResolver.ToFloat3(data.Brush.Target),
                    m_Start = EntityResolver.ToFloat3(data.Brush.Start),
                    m_Angle = data.Brush.Angle,
                    m_Size = data.Brush.Size,
                    m_Strength = data.Brush.Strength,
                    m_Time = data.Brush.Time,
                });
            }

            if (waypoints != null)
            {
                DynamicBuffer<Game.Routes.WaypointDefinition> buffer = _entities.AddBuffer<Game.Routes.WaypointDefinition>(entity);
                foreach (Game.Routes.WaypointDefinition waypoint in waypoints)
                {
                    buffer.Add(waypoint);
                }
            }

            if (data.Color != null)
            {
                _entities.AddComponentData(entity, new ColorDefinition { m_Color = new UnityEngine.Color32(data.Color.R, data.Color.G, data.Color.B, data.Color.A) });
            }

            _entities.AddComponent<Updated>(entity);
            return entity;
        }

        private CoursePos CreateCoursePos(CoursePosData data, StringBuilder problems)
        {
            float3 position = EntityResolver.ToFloat3(data.Position);
            Entity anchor = Entity.Null;
            float split = data.SplitPosition;
            if (data.Entity != null && data.Entity.Kind != EntityKind.None)
            {
                anchor = _resolver.Resolve(data.Entity, out string failure);
                if (anchor == Entity.Null)
                {
                    // The sender's junction is not here (an earlier piece may not have landed): join whatever net
                    // is under the point instead, so the piece still connects. Loose cable ends carry no power.
                    anchor = _resolver.FindAnchorNear(position, data.Entity.Prefab, out float t, out string what);
                    if (anchor != Entity.Null)
                    {
                        if (what == "edge")
                        {
                            split = t;
                        }

                        problems.Append("course anchor: ").Append(failure).Append("; joined the ").Append(what).Append(" under the point instead; ");
                    }
                    else
                    {
                        problems.Append("course anchor: ").Append(failure).Append("; nothing under the point to join; ");
                    }
                }
            }

            return new CoursePos
            {
                m_Entity = anchor,
                m_Position = position,
                m_Rotation = EntityResolver.ToQuaternion(data.Rotation),
                m_Elevation = new float2(data.ElevationX, data.ElevationY),
                m_CourseDelta = data.CourseDelta,
                m_SplitPosition = split,
                m_Flags = (CoursePosFlags)data.Flags,
                m_ParentMesh = data.ParentMesh,
            };
        }

        private Entity ResolveOrNote(EntityRef reference, string role, StringBuilder problems)
        {
            Entity entity = _resolver.Resolve(reference, out string failure);
            if (failure != null)
            {
                problems.Append(role).Append(": ").Append(failure).Append("; ");
            }

            return entity;
        }
    }
}
