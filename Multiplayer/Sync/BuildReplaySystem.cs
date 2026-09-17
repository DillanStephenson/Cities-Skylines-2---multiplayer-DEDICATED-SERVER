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
    ///   4. give the player their tool back once the queue is empty.
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
        /// <summary>Frames a queued command waits for the player to leave their tool before it is borrowed.</summary>
        private const int BorrowAfterFrames = 120;
        private int m_WaitedFrames;
        private ToolBaseSystem m_SavedTool;
        private PrefabBase m_SavedPrefab;
        private int m_InjectedCount;
        private int m_Batched;
        private string m_Problem;
        private int m_Replayed;
        private int m_Failed;
        private readonly List<Entity> m_Injected = new List<Entity>();

        public int QueueLength => m_Queue.Count;

        /// <summary>True while another player's build is being realised here (its temps are not the local player's preview).</summary>
        public bool IsApplying => m_Phase != Phase.Idle || m_SavedTool != null;

        public int ReplayedCount => m_Replayed;

        public int FailedCount => m_Failed;

        public sealed class QueuedBuild
        {
            public BuildCommand Command;
            public int FromPlayer;
            public bool CaptureAnyway;

            /// <summary>Times this command was held back because a prefab it needs has not arrived yet (a custom road, say).</summary>
            public int Waits;
        }

        /// <summary>How often, and how many times, a command waits for a prefab that another mod still has to create here.</summary>
        private const int PrefabWaitFrames = 60;
        private const int PrefabWaitLimit = 10;
        private int m_PrefabWaitUntilFrame;
        private int m_FrameCount;

        public void Enqueue(BuildCommand command, int fromPlayer, bool captureAnyway = false)
        {
            MultiplayerService service = Mod.Service;
            if (service != null && service.WorldSync.ResyncPending && !captureAnyway)
            {
                // This city has drifted and a fresh save is on its way; replaying into it now only makes it worse.
                Mod.log.Info("Not replaying " + command + " from player " + fromPlayer + ": waiting for the fresh save");
                return;
            }

            m_Queue.Add(new QueuedBuild { Command = command, FromPlayer = fromPlayer, CaptureAnyway = captureAnyway });
        }

        /// <summary>A build from another player could not be recreated here: the service decides whether that is drift.</summary>
        private void NoteFailure()
        {
            MultiplayerService service = Mod.Service;
            if (service != null && m_Current != null && m_Current.FromPlayer != service.Session.LocalPlayerId)
            {
                service.NoteReplayFailure();
            }
        }

        public void Clear()
        {
            m_Queue.Clear();
            if (m_Phase == Phase.Idle)
            {
                RestoreTool();
            }
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

            m_FrameCount++;
            switch (m_Phase)
            {
                case Phase.Idle:
                    if (m_Queue.Count == 0)
                    {
                        return;
                    }

                    if (m_FrameCount < m_PrefabWaitUntilFrame)
                    {
                        return;
                    }

                    if (WaitsForPrefab(m_Queue[0]))
                    {
                        m_PrefabWaitUntilFrame = m_FrameCount + PrefabWaitFrames;
                        return;
                    }

                    if (!PrepareTool())
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
                        Report(false, "the game here made nothing of it");
                        NoteFailure();
                        Finish();
                        return;
                    }

                    if (errors > 0)
                    {
                        string details = DescribeErrors(out bool blocking);
                        if (blocking)
                        {
                            Mod.log.Warn("Replay of " + m_Current.Command + ": " + errors + " of " + temps + " temp entities failed validation here; those parts will not be built" + details);
                            m_Problem = errors + " of " + temps + " parts failed validation here" + details;
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
                    Report(m_Problem == null, m_Problem ?? string.Empty);
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
        /// <summary>
        /// The one way that has proven safe: the default (selection) tool is made active while our temps are
        /// generated and applied, so nothing of the player's own preview is applied with them and no tool system
        /// is disabled or robbed of its definitions mid-frame. The player's tool comes back the moment the queue
        /// is empty. To keep the toolbar from resetting more than it must, a command waits up to
        /// <see cref="BorrowAfterFrames"/> for the player to leave their tool on their own, and every command
        /// queued meanwhile is applied in the same borrow.
        /// </summary>
        private bool PrepareTool()
        {
            ToolBaseSystem active = m_ToolSystem.activeTool;
            if (active == null)
            {
                return false;
            }

            if (active == m_DefaultTool)
            {
                m_WaitedFrames = 0;
                return true;
            }

            if (m_ToolSystem.applyMode == ApplyMode.Apply)
            {
                // The player is placing something this very frame; let that land first.
                return false;
            }

            if (++m_WaitedFrames < BorrowAfterFrames)
            {
                return false;
            }

            m_SavedTool = active;
            m_SavedPrefab = m_ToolSystem.activePrefab;
            m_ToolSystem.activeTool = m_DefaultTool;
            m_WaitedFrames = 0;
            return false;
        }

        private void RestoreTool()
        {
            if (m_SavedTool == null)
            {
                return;
            }

            try
            {
                if (m_SavedPrefab != null)
                {
                    m_ToolSystem.ActivatePrefabTool(m_SavedPrefab);
                }
                else
                {
                    m_ToolSystem.activeTool = m_SavedTool;
                }
            }
            catch (Exception ex)
            {
                Mod.log.Warn("Could not give the player their tool back: " + ex.Message);
            }

            m_SavedTool = null;
            m_SavedPrefab = null;
        }

        /// <summary>
        /// True when the command uses a prefab this game does not have yet and it is worth waiting a moment: another
        /// mod's runtime-made prefab (a Road Builder road) usually arrives through its own sync a second later.
        /// </summary>
        private bool WaitsForPrefab(QueuedBuild queued)
        {
            if (queued.Waits >= PrefabWaitLimit)
            {
                return false;
            }

            foreach (DefinitionData definition in queued.Command.Definitions)
            {
                if (definition.Prefab != null && !definition.Prefab.IsEmpty && m_Resolver.ResolvePrefab(definition.Prefab) == Entity.Null)
                {
                    queued.Waits++;
                    if (queued.Waits == 1)
                    {
                        Mod.log.Info("Replay of " + queued.Command + " waits for prefab '" + definition.Prefab + "' to turn up here");
                    }

                    return true;
                }
            }

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
            m_Problem = null;
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
                NoteFailure();
                m_Failed++;
                Report(false, "nothing could be recreated here" + (problems.Length > 0 ? ": " + problems : ""));
                Finish();
                return;
            }

            m_Phase = Phase.Injected;
        }

        /// <summary>Tell the builder how their command fared here. Nothing goes back for our own (dev) commands.</summary>
        private void Report(bool ok, string message)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || m_Current == null || m_Current.FromPlayer == service.Session.LocalPlayerId)
            {
                return;
            }

            try
            {
                service.SendBuildResult(new BuildResultCommand
                {
                    Sequence = m_Current.Command.Sequence,
                    BuilderPlayerId = m_Current.FromPlayer,
                    ToolId = m_Current.Command.ToolId,
                    Ok = ok,
                    Message = message ?? string.Empty,
                });
            }
            catch (Exception ex)
            {
                Mod.log.Warn("Could not report the replay result: " + ex.Message);
            }
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
                // Keep the borrowed tool until the queue drains: one toolbar change for the lot.
                return;
            }

            RestoreTool();
        }

        public bool IsBorrowingTool => m_SavedTool != null;

        private void SetApplyMode(ApplyMode mode)
        {
            ApplyModeSetter.Invoke(m_DefaultTool, new object[] { mode });
        }
    }
}
