using System;
using System.Collections.Generic;
using Game;
using Game.City;
using Game.Common;
using Game.Policies;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Simulation;
using Multiplayer.Core.Build;
using Multiplayer.Core.Session;
using Unity.Collections;
using Unity.Entities;

namespace Multiplayer.Sync
{
    /// <summary>Tag on policy events this mod has already dealt with (sent, or created from a remote change).</summary>
    public struct PolicySynced : IComponentData
    {
    }

    /// <summary>
    /// Policies set on one building, district or transport line through its panel. The game turns every such
    /// change into an event entity (Event + Modify) that its ModifiedSystem consumes in Modification4. This
    /// system runs just before that: new local events are described (target by prefab and position, policy by
    /// prefab) and sent; changes arriving from other players become identical event entities here, so the
    /// game applies them exactly as it would a click. Events for the city itself are left to
    /// <see cref="CityStateSyncSystem"/>, which also carries them in its periodic snapshot.
    /// </summary>
    public partial class PolicySyncSystem : GameSystemBase
    {
        private EntityQuery m_EventQuery;
        private EntityArchetype m_EventArchetype;
        private CitySystem m_CitySystem;
        private PrefabSystem m_PrefabSystem;
        private EntityResolver m_Resolver;
        private readonly List<PolicyCommand> m_Incoming = new List<PolicyCommand>();
        private int m_Sent;
        private int m_Applied;

        public int SentCount => m_Sent;

        public int AppliedCount => m_Applied;

        public void Receive(PolicyCommand command)
        {
            m_Incoming.Add(command);
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_CitySystem = World.GetOrCreateSystemManaged<CitySystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_Resolver = new EntityResolver(World, m_PrefabSystem);
            m_EventQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Event>(), ComponentType.ReadOnly<Modify>() },
                None = new[] { ComponentType.ReadOnly<PolicySynced>() },
            });
            m_EventArchetype = EntityManager.CreateArchetype(ComponentType.ReadWrite<Event>(), ComponentType.ReadWrite<Modify>(), ComponentType.ReadWrite<PolicySynced>());
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            GameManager manager = GameManager.instance;
            if (service == null || service.Session.State != SessionState.Connected || manager == null || manager.gameMode != GameMode.Game || manager.isGameLoading)
            {
                m_Incoming.Clear();
                return;
            }

            if (m_Incoming.Count > 0)
            {
                ApplyIncoming();
            }

            if (!m_EventQuery.IsEmptyIgnoreFilter)
            {
                CaptureLocal(service);
            }
        }

        private void CaptureLocal(MultiplayerService service)
        {
            Entity city = m_CitySystem.City;
            using (NativeArray<Entity> events = m_EventQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < events.Length; i++)
                {
                    Modify modify = EntityManager.GetComponentData<Modify>(events[i]);
                    EntityManager.AddComponent<PolicySynced>(events[i]);
                    if (modify.m_Entity == city)
                    {
                        continue;
                    }

                    var command = new PolicyCommand
                    {
                        Target = m_Resolver.Describe(modify.m_Entity),
                        Policy = m_Resolver.DescribePrefab(modify.m_Policy),
                        Active = (modify.m_Flags & PolicyFlags.Active) != 0,
                        Adjustment = modify.m_Adjustment,
                    };

                    if (command.Target.Kind == EntityKind.None || command.Target.Kind == EntityKind.Unresolvable || command.Policy.IsEmpty)
                    {
                        Mod.log.Info("Policy " + command.Policy + " changed on something this mod cannot describe yet (" + command.Target.Kind + "); not synced");
                        continue;
                    }

                    try
                    {
                        service.SendPolicy(command);
                        m_Sent++;
                    }
                    catch (Exception ex)
                    {
                        Mod.log.Warn("Could not send " + command + ": " + ex.Message);
                    }
                }
            }
        }

        private void ApplyIncoming()
        {
            var commands = new List<PolicyCommand>(m_Incoming);
            m_Incoming.Clear();
            foreach (PolicyCommand command in commands)
            {
                try
                {
                    Entity target = m_Resolver.Resolve(command.Target, out string failure);
                    Entity policy = m_Resolver.ResolvePrefab(command.Policy);
                    if (target == Entity.Null || policy == Entity.Null)
                    {
                        Mod.log.Warn("Could not apply " + command + ": " + (target == Entity.Null ? failure ?? "target missing" : "policy prefab not found here"));
                        continue;
                    }

                    Entity e = EntityManager.CreateEntity(m_EventArchetype);
                    EntityManager.SetComponentData(e, new Modify(target, policy, command.Active, command.Adjustment));
                    m_Applied++;
                    Mod.log.Info("Applied " + command);
                }
                catch (Exception ex)
                {
                    Mod.log.Warn("Could not apply " + command + ": " + ex.Message);
                }
            }
        }
    }
}
