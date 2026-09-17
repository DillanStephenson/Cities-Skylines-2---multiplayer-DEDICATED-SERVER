using System;
using System.Collections.Generic;
using System.Globalization;
using Game;
using Game.City;
using Game.Economy;
using Game.Policies;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Simulation;
using Game.UI.InGame;
using Multiplayer.Core.Build;
using Multiplayer.Core.Session;
using Unity.Collections;
using Unity.Entities;

namespace Multiplayer.Sync
{
    /// <summary>
    /// Keeps panel-driven city settings in step: tax rates, service budgets, service fees and city policies.
    /// Twice a second the current values are compared with the last synced ones; what changed locally is
    /// broadcast, and what arrives from others is applied through the game's own setters and recorded as
    /// synced so it is not sent back. The owner also broadcasts a full snapshot once a minute so late
    /// joiners converge without a world reload.
    /// </summary>
    public partial class CityStateSyncSystem : GameSystemBase
    {
        private const int CheckIntervalFrames = 30;
        private const int SnapshotIntervalFrames = 3600;

        /// <summary>How often the leader sends the treasury balance; everyone else adopts it.</summary>
        private const int MoneyIntervalFrames = 300;
        private const int ResidentialLevels = 5;

        private TaxSystem m_TaxSystem;
        private CityServiceBudgetSystem m_BudgetSystem;
        private CitySystem m_CitySystem;
        private PoliciesUISystem m_PoliciesUISystem;
        private PrefabSystem m_PrefabSystem;
        private DevTreeSystem m_DevTreeSystem;
        private EntityResolver m_Resolver;
        private EntityQuery m_BudgetServiceQuery;
        private EntityQuery m_DevNodeQuery;

        /// <summary>Development nodes another player bought that could not be bought here yet (a prerequisite still locked): retried each check.</summary>
        private readonly Dictionary<string, int> m_PendingDevNodes = new Dictionary<string, int>(StringComparer.Ordinal);
        private const int DevNodeRetries = 120;

        private readonly Dictionary<string, float> m_Last = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly List<CityStateCommand> m_Incoming = new List<CityStateCommand>();
        private bool m_Primed;
        private bool m_ResyncAfterApply;
        private int m_Frame;
        private int m_MoneyFrame;
        private int m_Sent;
        private int m_Applied;

        public int SentCount => m_Sent;

        public int AppliedCount => m_Applied;

        public void Receive(CityStateCommand command)
        {
            m_Incoming.Add(command);
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_TaxSystem = World.GetOrCreateSystemManaged<TaxSystem>();
            m_BudgetSystem = World.GetOrCreateSystemManaged<CityServiceBudgetSystem>();
            m_CitySystem = World.GetOrCreateSystemManaged<CitySystem>();
            m_PoliciesUISystem = World.GetOrCreateSystemManaged<PoliciesUISystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_Resolver = new EntityResolver(World, m_PrefabSystem);
            m_DevTreeSystem = World.GetOrCreateSystemManaged<DevTreeSystem>();
            m_BudgetServiceQuery = GetEntityQuery(ComponentType.ReadOnly<CollectedCityServiceBudgetData>(), ComponentType.ReadOnly<PrefabData>());
            m_DevNodeQuery = GetEntityQuery(ComponentType.ReadOnly<DevTreeNodeData>(), ComponentType.ReadOnly<PrefabData>());
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            GameManager manager = GameManager.instance;
            if (service == null || service.Session.State != SessionState.Connected || manager == null || manager.gameMode != GameMode.Game || manager.isGameLoading)
            {
                m_Primed = false;
                m_Incoming.Clear();
                return;
            }

            m_Frame++;
            if (m_Incoming.Count > 0)
            {
                ApplyIncoming();
            }

            if (m_Frame % CheckIntervalFrames != 0)
            {
                return;
            }

            if (m_PendingDevNodes.Count > 0)
            {
                RetryPendingDevNodes();
            }

            Dictionary<string, float> current;
            try
            {
                current = Snapshot();
            }
            catch (Exception ex)
            {
                Mod.log.Warn("City state snapshot failed: " + ex.Message);
                return;
            }

            if (!m_Primed || m_ResyncAfterApply)
            {
                // First look, or right after applying someone else's change: adopt the current values as synced.
                // A remote change can move derived values too (an area tax rate sets every level under it), and
                // those must not be mistaken for local edits.
                m_Primed = true;
                m_ResyncAfterApply = false;
                m_Last.Clear();
                foreach (KeyValuePair<string, float> pair in current)
                {
                    m_Last[pair.Key] = pair.Value;
                }

                return;
            }

            bool snapshotDue = service.Session.IsOwner && m_Frame % SnapshotIntervalFrames == 0;
            var command = new CityStateCommand { FullSnapshot = snapshotDue };
            foreach (KeyValuePair<string, float> pair in current)
            {
                if (snapshotDue || !m_Last.TryGetValue(pair.Key, out float last) || !Same(last, pair.Value))
                {
                    command.Entries.Add(new StateEntry(pair.Key, pair.Value));
                }

                m_Last[pair.Key] = pair.Value;
            }

            // Money is the one number that drifts fastest between PCs (each simulates its own income and
            // upkeep). The leader's balance is the shared one; others overwrite theirs with it.
            if (service.IsLeader)
            {
                m_MoneyFrame += CheckIntervalFrames;
                if (m_MoneyFrame >= MoneyIntervalFrames)
                {
                    m_MoneyFrame = 0;
                    if (TryReadMoney(out int balance))
                    {
                        command.Entries.Add(new StateEntry("money", balance));
                    }

                    // Progress drifts the same way: each PC earns its own XP. The leader's XP and development
                    // points are the shared ones; milestones then fire on every PC at the same XP.
                    if (TryReadXp(out int xp))
                    {
                        command.Entries.Add(new StateEntry("xp", xp));
                        command.Entries.Add(new StateEntry("devpoints", m_DevTreeSystem.points));
                    }
                }
            }

            if (command.Entries.Count == 0)
            {
                return;
            }

            service.SendCityState(command);
            m_Sent++;
        }

        // ------------------------------------------------------------------ snapshot

        private Dictionary<string, float> Snapshot()
        {
            var state = new Dictionary<string, float>(StringComparer.Ordinal);

            foreach (TaxAreaType area in new[] { TaxAreaType.Residential, TaxAreaType.Commercial, TaxAreaType.Industrial, TaxAreaType.Office })
            {
                state["tax:area:" + area] = m_TaxSystem.GetTaxRate(area);
            }

            for (int level = 0; level < ResidentialLevels; level++)
            {
                state["tax:res:" + level] = m_TaxSystem.GetResidentialTaxRate(level);
            }

            ResourceIterator iterator = ResourceIterator.GetIterator();
            while (iterator.Next())
            {
                string name = iterator.resource.ToString();
                state["tax:com:" + name] = m_TaxSystem.GetCommercialTaxRate(iterator.resource);
                state["tax:ind:" + name] = m_TaxSystem.GetIndustrialTaxRate(iterator.resource);
                state["tax:off:" + name] = m_TaxSystem.GetOfficeTaxRate(iterator.resource);
            }

            using (NativeArray<Entity> services = m_BudgetServiceQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < services.Length; i++)
                {
                    PrefabKey key = m_Resolver.DescribePrefab(services[i]);
                    if (!key.IsEmpty)
                    {
                        state["budget:" + key.Type + "|" + key.Name] = m_BudgetSystem.GetServiceBudget(services[i]);
                    }
                }
            }

            Entity city = m_CitySystem.City;
            if (city != Entity.Null && EntityManager.HasBuffer<ServiceFee>(city))
            {
                DynamicBuffer<ServiceFee> fees = EntityManager.GetBuffer<ServiceFee>(city, true);
                foreach (PlayerResource resource in Enum.GetValues(typeof(PlayerResource)))
                {
                    state["fee:" + resource] = ServiceFeeSystem.GetFee(resource, fees);
                }
            }

            if (city != Entity.Null && EntityManager.HasBuffer<Policy>(city))
            {
                DynamicBuffer<Policy> policies = EntityManager.GetBuffer<Policy>(city, true);
                for (int i = 0; i < policies.Length; i++)
                {
                    PrefabKey key = m_Resolver.DescribePrefab(policies[i].m_Policy);
                    if (key.IsEmpty)
                    {
                        continue;
                    }

                    string prefix = "policy:" + key.Type + "|" + key.Name;
                    state[prefix + ":active"] = (policies[i].m_Flags & PolicyFlags.Active) != 0 ? 1f : 0f;
                    state[prefix + ":adj"] = policies[i].m_Adjustment;
                }
            }

            // Development tree: which nodes have been bought. A node going from locked to unlocked here is a purchase.
            using (NativeArray<Entity> nodes = m_DevNodeQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < nodes.Length; i++)
                {
                    string name = DevNodeName(nodes[i]);
                    if (name == null)
                    {
                        continue;
                    }

                    bool locked = EntityManager.HasComponent<Locked>(nodes[i]) && EntityManager.IsComponentEnabled<Locked>(nodes[i]);
                    state["devnode:" + name] = locked ? 0f : 1f;
                }
            }

            return state;
        }

        // ------------------------------------------------------------------ apply

        private void ApplyIncoming()
        {
            List<CityStateCommand> commands = new List<CityStateCommand>(m_Incoming);
            m_Incoming.Clear();
            foreach (CityStateCommand command in commands)
            {
                int applied = 0;
                foreach (StateEntry entry in command.Entries)
                {
                    if (m_Last.TryGetValue(entry.Key, out float last) && Same(last, entry.Value))
                    {
                        continue;
                    }

                    try
                    {
                        if (Apply(entry.Key, entry.Value))
                        {
                            applied++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Mod.log.Warn("Could not apply city state " + entry + ": " + ex.Message);
                    }

                    m_Last[entry.Key] = entry.Value;
                }

                m_Applied += applied;
                if (applied > 0)
                {
                    m_ResyncAfterApply = true;
                }

                if (applied > 0 || !command.FullSnapshot)
                {
                    Mod.log.Info("Applied " + applied + " of " + command.Entries.Count + " city state entries" + (command.FullSnapshot ? " (snapshot)" : ""));
                }
            }
        }

        private bool Apply(string key, float value)
        {
            string[] parts = key.Split(':');
            switch (parts[0])
            {
                case "money":
                    return ApplyMoney((int)Math.Round(value));

                case "xp":
                    return ApplyXp((int)Math.Round(value));

                case "devpoints":
                    return ApplyDevPoints((int)Math.Round(value));

                case "devnode":
                    // Only "bought" travels; nothing ever locks a node again.
                    return parts.Length >= 2 && value > 0.5f && ApplyDevNode(key.Substring(key.IndexOf(':') + 1), true);
            }

            if (parts.Length < 2)
            {
                return false;
            }

            switch (parts[0])
            {

                case "tax":
                    return ApplyTax(parts, value);

                case "budget":
                {
                    Entity prefab = ResolvePrefab(parts[1]);
                    if (prefab == Entity.Null)
                    {
                        return false;
                    }

                    m_BudgetSystem.SetServiceBudget(prefab, (int)Math.Round(value));
                    return true;
                }

                case "fee":
                {
                    Entity city = m_CitySystem.City;
                    if (city == Entity.Null || !EntityManager.HasBuffer<ServiceFee>(city) || !Enum.TryParse(parts[1], out PlayerResource resource))
                    {
                        return false;
                    }

                    DynamicBuffer<ServiceFee> fees = EntityManager.GetBuffer<ServiceFee>(city);
                    ServiceFeeSystem.SetFee(resource, fees, value);
                    return true;
                }

                case "policy":
                {
                    if (parts.Length < 3)
                    {
                        return false;
                    }

                    Entity prefab = ResolvePrefab(parts[1]);
                    if (prefab == Entity.Null)
                    {
                        return false;
                    }

                    ReadPolicy(prefab, out bool active, out float adjustment);
                    if (parts[2] == "active")
                    {
                        active = value > 0.5f;
                    }
                    else if (parts[2] == "adj")
                    {
                        adjustment = value;
                    }
                    else
                    {
                        return false;
                    }

                    m_PoliciesUISystem.SetCityPolicy(prefab, active, adjustment);
                    return true;
                }

                default:
                    return false;
            }
        }

        private bool ApplyTax(string[] parts, float value)
        {
            if (parts.Length < 3)
            {
                return false;
            }

            int rate = (int)Math.Round(value);
            switch (parts[1])
            {
                case "area":
                    if (!Enum.TryParse(parts[2], out TaxAreaType area))
                    {
                        return false;
                    }

                    m_TaxSystem.SetTaxRate(area, rate);
                    return true;

                case "res":
                    if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int level))
                    {
                        return false;
                    }

                    m_TaxSystem.SetResidentialTaxRate(level, rate);
                    return true;

                case "com":
                case "ind":
                case "off":
                {
                    if (!Enum.TryParse(parts[2], out Resource resource))
                    {
                        return false;
                    }

                    if (parts[1] == "com")
                    {
                        m_TaxSystem.SetCommercialTaxRate(resource, rate);
                    }
                    else if (parts[1] == "ind")
                    {
                        m_TaxSystem.SetIndustrialTaxRate(resource, rate);
                    }
                    else
                    {
                        m_TaxSystem.SetOfficeTaxRate(resource, rate);
                    }

                    return true;
                }

                default:
                    return false;
            }
        }

        private bool TryReadMoney(out int balance)
        {
            balance = 0;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.HasComponent<PlayerMoney>(city))
            {
                return false;
            }

            PlayerMoney money = EntityManager.GetComponentData<PlayerMoney>(city);
            if (money.m_Unlimited)
            {
                return false;
            }

            balance = money.money;
            return true;
        }

        private bool ApplyMoney(int balance)
        {
            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.HasComponent<PlayerMoney>(city))
            {
                return false;
            }

            PlayerMoney current = EntityManager.GetComponentData<PlayerMoney>(city);
            if (current.m_Unlimited || current.money == balance)
            {
                return false;
            }

            EntityManager.SetComponentData(city, new PlayerMoney(balance));
            return true;
        }

        private void ReadPolicy(Entity prefab, out bool active, out float adjustment)
        {
            active = false;
            adjustment = 0f;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.HasBuffer<Policy>(city))
            {
                return;
            }

            DynamicBuffer<Policy> policies = EntityManager.GetBuffer<Policy>(city, true);
            for (int i = 0; i < policies.Length; i++)
            {
                if (policies[i].m_Policy == prefab)
                {
                    active = (policies[i].m_Flags & PolicyFlags.Active) != 0;
                    adjustment = policies[i].m_Adjustment;
                    return;
                }
            }
        }

        // ------------------------------------------------------------------ progression

        private bool TryReadXp(out int xp)
        {
            xp = 0;
            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.HasComponent<XP>(city))
            {
                return false;
            }

            xp = EntityManager.GetComponentData<XP>(city).m_XP;
            return true;
        }

        private bool ApplyXp(int xp)
        {
            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.HasComponent<XP>(city))
            {
                return false;
            }

            if (EntityManager.GetComponentData<XP>(city).m_XP == xp)
            {
                return false;
            }

            EntityManager.SetComponentData(city, new XP { m_XP = xp });
            return true;
        }

        private bool ApplyDevPoints(int points)
        {
            if (m_DevTreeSystem.points == points)
            {
                return false;
            }

            m_DevTreeSystem.points = points;
            return true;
        }

        private string DevNodeName(Entity node)
        {
            try
            {
                PrefabBase prefab = m_PrefabSystem.GetPrefab<PrefabBase>(node);
                return prefab != null ? prefab.name : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private Entity FindDevNode(string name)
        {
            if (m_PrefabSystem.TryGetPrefab(new PrefabID("DevTreeNodePrefab", name), out PrefabBase prefab) && m_PrefabSystem.TryGetEntity(prefab, out Entity node))
            {
                return node;
            }

            return Entity.Null;
        }

        /// <summary>
        /// Buys the node here the way the other player bought it there: the points they paid are matched so the
        /// game's own purchase goes through and deducts them; the unlock itself is the game's, end of frame.
        /// </summary>
        private bool ApplyDevNode(string name, bool queueRetry)
        {
            Entity node = FindDevNode(name);
            if (node == Entity.Null)
            {
                Mod.log.Warn("Development node '" + name + "' does not exist here");
                return false;
            }

            if (!EntityManager.HasComponent<Locked>(node) || !EntityManager.IsComponentEnabled<Locked>(node))
            {
                m_PendingDevNodes.Remove(name);
                return false;
            }

            DevTreeNodeData data = EntityManager.GetComponentData<DevTreeNodeData>(node);
            if (m_DevTreeSystem.points < data.m_Cost)
            {
                m_DevTreeSystem.points = data.m_Cost;
            }

            int before = m_DevTreeSystem.points;
            m_DevTreeSystem.Purchase(node);
            if (m_DevTreeSystem.points < before)
            {
                m_PendingDevNodes.Remove(name);
                Mod.log.Info("Bought development node '" + name + "' to match the other player (" + data.m_Cost + " points)");
                return true;
            }

            if (queueRetry && !m_PendingDevNodes.ContainsKey(name))
            {
                // A prerequisite is still locked here, most likely; it lands a moment later.
                m_PendingDevNodes[name] = DevNodeRetries;
            }

            return false;
        }

        private void RetryPendingDevNodes()
        {
            var names = new List<string>(m_PendingDevNodes.Keys);
            foreach (string name in names)
            {
                if (ApplyDevNode(name, false))
                {
                    m_ResyncAfterApply = true;
                    continue;
                }

                int left = m_PendingDevNodes.ContainsKey(name) ? m_PendingDevNodes[name] - 1 : 0;
                if (left <= 0)
                {
                    m_PendingDevNodes.Remove(name);
                    Mod.log.Warn("Development node '" + name + "' could not be bought here; it will come with the next save");
                }
                else
                {
                    m_PendingDevNodes[name] = left;
                }
            }
        }

        private Entity ResolvePrefab(string typeAndName)
        {
            int bar = typeAndName.IndexOf('|');
            if (bar <= 0)
            {
                return Entity.Null;
            }

            return m_Resolver.ResolvePrefab(new PrefabKey { Type = typeAndName.Substring(0, bar), Name = typeAndName.Substring(bar + 1) });
        }

        private static bool Same(float a, float b)
        {
            return Math.Abs(a - b) < 0.0005f;
        }
    }
}
