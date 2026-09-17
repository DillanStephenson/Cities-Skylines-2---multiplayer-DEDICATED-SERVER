using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Game;
using Game.Common;
using Game.Notifications;
using Game.Prefabs;
using Game.Tools;
using Multiplayer.Core.Build;
using Unity.Entities;

namespace Multiplayer.Sync
{
    /// <summary>
    /// Applies other players' build commands. Runs in the ToolUpdate phase right before the game's
    /// ToolOutputSystem, which is the one place where the active tool's apply mode can be overridden for
    /// the frame. Sequence per command:
    ///   1. make sure the default (selection) tool is active and no temp entities are lying around;
    ///   2. create the definition entities; the game's generators turn them into temps later this frame;
    ///   3. next frame, set the default tool's apply mode to Apply so the output system realises the temps;
    ///   4. give the player their tool back.
    /// The capture system is told to stay quiet during step 3 so nothing is echoed to the server.
    /// </summary>
    public partial class BuildReplaySystem : GameSystemBase
    {
        private enum Phase
        {
            Idle,
            WaitingForClear,
            Injected,
            Applied,
        }

        /// <summary>How long a queued command waits for the player to leave their tool before it is borrowed.</summary>
        private const int HijackAfterFrames = 90;

        private static readonly MethodInfo ApplyModeSetter = typeof(ToolBaseSystem).GetProperty("applyMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetSetMethod(true);

        // A List used as a FIFO: Queue<T> is ambiguous between the game's mscorlib and System.dll on net48.
        private readonly List<QueuedBuild> m_Queue = new List<QueuedBuild>();

        private ToolSystem m_ToolSystem;
        private DefaultToolSystem m_DefaultTool;
        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_TempQuery;
        private EntityQuery m_TempErrorQuery;
        private EntityQuery m_TempIconQuery;
        private EntityResolver m_Resolver;
        private DefinitionCodec m_Codec;

        private Phase m_Phase = Phase.Idle;
        private QueuedBuild m_Current;
        private int m_WaitedFrames;
        private ToolBaseSystem m_SavedTool;
        private PrefabBase m_SavedPrefab;
        private int m_InjectedCount;
        private int m_Batched;
        private int m_Replayed;
        private int m_Failed;
        private readonly List<Entity> m_Injected = new List<Entity>();

        public int QueueLength => m_Queue.Count;

        public int ReplayedCount => m_Replayed;

        public int FailedCount => m_Failed;

        public sealed class QueuedBuild
        {
            public BuildCommand Command;
            public int FromPlayer;
            public bool CaptureAnyway;
        }

        public void Enqueue(BuildCommand command, int fromPlayer, bool captureAnyway = false)
        {
            m_Queue.Add(new QueuedBuild { Command = command, FromPlayer = fromPlayer, CaptureAnyway = captureAnyway });
        }

        public void Clear()
        {
            m_Queue.Clear();
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_DefaultTool = World.GetOrCreateSystemManaged<DefaultToolSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_TempQuery = GetEntityQuery(ComponentType.ReadOnly<Temp>());
            m_TempErrorQuery = GetEntityQuery(ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Error>());
            m_TempIconQuery = GetEntityQuery(ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Icon>(), ComponentType.ReadOnly<PrefabRef>());
            m_Resolver = new EntityResolver(World, m_PrefabSystem);
            m_Codec = new DefinitionCodec(EntityManager, m_Resolver);
            if (ApplyModeSetter == null)
            {
                Mod.log.Error("ToolBaseSystem.applyMode setter not found; build replay cannot work on this game version");
            }
        }

        protected override void OnUpdate()
        {
            // The guard covers exactly one apply frame.
            SyncGuard.IsReplaying = false;
            SyncGuard.CaptureAnyway = false;

            if (ApplyModeSetter == null)
            {
                return;
            }

            switch (m_Phase)
            {
                case Phase.Idle:
                    if (m_Queue.Count == 0)
                    {
                        return;
                    }

                    if (!EnsureDefaultTool())
                    {
                        return;
                    }

                    if (!m_TempQuery.IsEmptyIgnoreFilter)
                    {
                        // Something still previewing (the borrowed tool's last frame, usually): clear it first.
                        SetApplyMode(ApplyMode.Clear);
                        m_Phase = Phase.WaitingForClear;
                        return;
                    }

                    Inject();
                    return;

                case Phase.WaitingForClear:
                    if (!m_TempQuery.IsEmptyIgnoreFilter)
                    {
                        SetApplyMode(ApplyMode.Clear);
                        return;
                    }

                    Inject();
                    return;

                case Phase.Injected:
                {
                    int errors = m_TempErrorQuery.CalculateEntityCount();
                    int temps = m_TempQuery.CalculateEntityCount();
                    if (temps == 0)
                    {
                        Mod.log.Warn("Replay of " + m_Current.Command + ": the game generated nothing from " + m_InjectedCount + " definitions");
                        m_Failed++;
                        Finish();
                        return;
                    }

                    if (errors > 0)
                    {
                        string details = DescribeErrors(out bool blocking);
                        if (blocking)
                        {
                            Mod.log.Warn("Replay of " + m_Current.Command + ": " + errors + " of " + temps + " temp entities failed validation here; those parts will not be built" + details);
                        }
                        else
                        {
                            Mod.log.Info("Replay of " + m_Current.Command + ": " + errors + " of " + temps + " temp entities carry a warning here" + details);
                        }
                    }

                    SyncGuard.IsReplaying = true;
                    SyncGuard.CaptureAnyway = m_Current.CaptureAnyway;
                    SetApplyMode(ApplyMode.Apply);
                    m_Phase = Phase.Applied;
                    return;
                }

                case Phase.Applied:
                    m_Replayed += 1 + m_Batched;
                    Mod.log.Info("Replayed " + m_Current.Command + " from player " + m_Current.FromPlayer + " (" + m_InjectedCount + " definitions" + (m_Batched > 0 ? ", " + m_Batched + " more stroke command(s) with it" : "") + ")");
                    Finish();
                    return;
            }
        }

        /// <summary>The validation icons the game raised on the current temps: error type and the prefab it hit.</summary>
        private string DescribeErrors(out bool blocking)
        {
            blocking = false;
            try
            {
                var parts = new List<string>();
                using (Unity.Collections.NativeArray<Entity> icons = m_TempIconQuery.ToEntityArray(Unity.Collections.Allocator.Temp))
                {
                    for (int i = 0; i < icons.Length && parts.Count < 8; i++)
                    {
                        PrefabRef iconPrefab = EntityManager.GetComponentData<PrefabRef>(icons[i]);
                        bool isError = EntityManager.GetComponentData<Icon>(icons[i]).m_Priority >= IconPriority.Error;
                        blocking |= isError;
                        string error = EntityManager.HasComponent<ToolErrorData>(iconPrefab.m_Prefab)
                            ? EntityManager.GetComponentData<ToolErrorData>(iconPrefab.m_Prefab).m_Error.ToString()
                            : m_Resolver.DescribePrefab(iconPrefab.m_Prefab).Name;
                        string on = "";
                        if (EntityManager.HasComponent<Owner>(icons[i]))
                        {
                            Entity owner = EntityManager.GetComponentData<Owner>(icons[i]).m_Owner;
                            if (EntityManager.HasComponent<PrefabRef>(owner))
                            {
                                on = " on " + m_Resolver.DescribePrefab(EntityManager.GetComponentData<PrefabRef>(owner).m_Prefab).Name;
                            }
                        }

                        if (EntityManager.HasComponent<Target>(icons[i]))
                        {
                            Entity target = EntityManager.GetComponentData<Target>(icons[i]).m_Target;
                            if (target != Entity.Null && EntityManager.HasComponent<PrefabRef>(target))
                            {
                                on += " against " + m_Resolver.DescribePrefab(EntityManager.GetComponentData<PrefabRef>(target).m_Prefab).Name;
                            }
                        }

                        parts.Add((isError ? "error " : "warning ") + error + on);
                    }
                }

                return parts.Count == 0 ? "" : " [" + string.Join("; ", parts.ToArray()) + "]";
            }
            catch (Exception ex)
            {
                return " [could not describe: " + ex.Message + "]";
            }
        }

        /// <summary>True when the default tool is active. Otherwise waits a little for the player, then borrows the tool.</summary>
        private bool EnsureDefaultTool()
        {
            if (m_ToolSystem.activeTool == m_DefaultTool)
            {
                m_WaitedFrames = 0;
                return true;
            }

            if (++m_WaitedFrames < HijackAfterFrames)
            {
                return false;
            }

            m_SavedTool = m_ToolSystem.activeTool;
            m_SavedPrefab = m_ToolSystem.activePrefab;
            m_ToolSystem.activeTool = m_DefaultTool;
            Mod.log.Info("Borrowing the tool for a moment to apply " + m_Queue.Count + " queued build(s)");
            m_WaitedFrames = 0;
            return false;
        }

        /// <summary>Terraforming strokes only: independent of each other, so several can be applied in one frame.</summary>
        private static bool IsStrokesOnly(BuildCommand command)
        {
            if (command.Definitions.Count == 0)
            {
                return false;
            }

            foreach (DefinitionData definition in command.Definitions)
            {
                if (definition.Brush == null)
                {
                    return false;
                }
            }

            return true;
        }

        private void Inject()
        {
            m_Current = m_Queue[0];
            m_Queue.RemoveAt(0);
            var problems = new StringBuilder();
            m_InjectedCount = 0;
            m_Batched = 0;
            m_Injected.Clear();
            InjectDefinitions(m_Current.Command, problems);

            // The terrain tool sends one stroke per frame while the mouse is held. Replaying them one command
            // per two frames would fall behind a long drag, so consecutive strokes are applied together.
            while (m_Batched < 30 && IsStrokesOnly(m_Current.Command) && m_Queue.Count > 0 && IsStrokesOnly(m_Queue[0].Command))
            {
                QueuedBuild next = m_Queue[0];
                m_Queue.RemoveAt(0);
                InjectDefinitions(next.Command, problems);
                m_Batched++;
            }

            if (problems.Length > 0)
            {
                Mod.log.Warn("Replay of " + m_Current.Command + ": " + problems);
            }

            if (m_InjectedCount == 0)
            {
                Mod.log.Warn("Replay of " + m_Current.Command + " skipped: nothing could be recreated here");
                m_Failed++;
                Finish();
                return;
            }

            m_Phase = Phase.Injected;
        }

        private void InjectDefinitions(BuildCommand command, StringBuilder problems)
        {
            foreach (DefinitionData definition in command.Definitions)
            {
                try
                {
                    Entity created = m_Codec.Create(definition, problems);
                    if (created != Entity.Null)
                    {
                        m_InjectedCount++;
                        m_Injected.Add(created);
                    }
                }
                catch (Exception ex)
                {
                    problems.Append("exception: ").Append(ex.Message).Append("; ");
                }
            }
        }

        private void Finish()
        {
            // The generators consumed the definitions in the inject frame; nothing else cleans them up when
            // no tool owns them, and a later capture would pick stale ones up again.
            foreach (Entity definition in m_Injected)
            {
                if (EntityManager.Exists(definition))
                {
                    EntityManager.DestroyEntity(definition);
                }
            }

            m_Injected.Clear();
            m_Phase = Phase.Idle;
            m_Current = null;
            if (m_Queue.Count > 0)
            {
                // Keep the borrowed tool until the queue drains.
                return;
            }

            if (m_SavedTool != null)
            {
                if (m_SavedPrefab != null)
                {
                    m_ToolSystem.ActivatePrefabTool(m_SavedPrefab);
                }
                else
                {
                    m_ToolSystem.activeTool = m_SavedTool;
                }

                m_SavedTool = null;
                m_SavedPrefab = null;
            }
        }

        private void SetApplyMode(ApplyMode mode)
        {
            ApplyModeSetter.Invoke(m_DefaultTool, new object[] { mode });
        }
    }
}
