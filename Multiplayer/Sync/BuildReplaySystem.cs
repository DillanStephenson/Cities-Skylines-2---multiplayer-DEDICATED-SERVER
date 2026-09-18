using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Game;
using Game.Common;
using Game.Input;
using Game.Notifications;
using Game.Prefabs;
using Game.SceneFlow;
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

        /// <summary>
        /// The tool system's private active-tool slot. Writing it directly swaps the running tool the same way
        /// the property does (ToolUpdate compares the slot every frame) without raising EventToolChanged, so the
        /// UI never hears about the borrow: the tool options panel and the toolbar selection stay put.
        /// </summary>
        private static readonly FieldInfo ActiveToolField = typeof(ToolSystem).GetField("m_ActiveTool", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>The tool's apply action (the mouse button, usually): while it is held the player is mid-drag.</summary>
        private static readonly PropertyInfo ApplyActionProperty = typeof(ToolBaseSystem).GetProperty("applyAction", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

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

        /// <summary>A held mouse button (a road being dragged out, a zone being painted) is never interrupted for this long.</summary>
        private const int HeldButtonWaitFrames = 900;
        private int m_WaitedFrames;
        private int m_HeldFrames;
        private bool m_ModeSetThisFrame;
        private ToolBaseSystem m_SavedTool;
        private PrefabBase m_SavedPrefab;
        private int m_InjectedCount;
        private int m_Batched;
        private string m_Problem;
        private int m_Replayed;
        private int m_Failed;

        // The ledger. Every build that arrives ends up in exactly one of these, so a session can be checked
        // afterwards by arithmetic instead of by reading the log: received == replayed + batched + skipped,
        // and partial says how many of the replayed ones the game here only half-accepted.
        private int m_Received;
        private int m_BatchedTotal;
        private int m_Skipped;
        private int m_Partial;
        private int m_Retried;
        private int m_LedgerLoggedAt;

        /// <summary>Roughly every two minutes at 60 fps.</summary>
        private const int LedgerEveryFrames = 7200;

        public int ReceivedCount => m_Received;

        public int BatchedCount => m_BatchedTotal;

        public int SkippedCount => m_Skipped;

        public int PartialCount => m_Partial;

        public int RetriedCount => m_Retried;

        /// <summary>One line the player or the log can be shown: what happened to every build that arrived.</summary>
        public string Ledger()
        {
            int accounted = m_Replayed + m_BatchedTotal + m_Skipped;
            return "received " + m_Received
                + ", replayed " + m_Replayed
                + ", batched " + m_BatchedTotal
                + ", part-built " + m_Partial
                + ", retried " + m_Retried
                + ", given up " + m_Skipped
                + ", waiting " + m_Queue.Count
                + (accounted + m_Queue.Count == m_Received ? "" : " (UNACCOUNTED " + (m_Received - accounted - m_Queue.Count) + ")");
        }
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

            /// <summary>Times this command was put back because nothing here could be recreated from it yet.</summary>
            public int AnchorWaits;
        }

        /// <summary>How often, and how many times, a command waits for a prefab that another mod still has to create here.</summary>
        private const int PrefabWaitFrames = 60;
        private const int PrefabWaitLimit = 10;
        private int m_PrefabWaitUntilFrame;
        private int m_FrameCount;

        /// <summary>
        /// How long a build waits for the thing it attaches to before it is given up on: about half a second
        /// between tries, twenty tries, so roughly ten seconds. Long enough for the build that creates the
        /// anchor to arrive and land, short enough that a genuinely impossible build is reported while the
        /// builder still remembers making it.
        /// </summary>
        private const int AnchorRetryFrames = 30;
        private const int AnchorRetryLimit = 20;
        private int m_AnchorRetryUntilFrame;

        public void Enqueue(BuildCommand command, int fromPlayer, bool captureAnyway = false)
        {
            MultiplayerService service = Mod.Service;
            if (service != null && service.WorldSync.HoldReplays && !captureAnyway)
            {
                // This city has drifted and a fresh save is on its way; replaying into it now only makes it worse.
                Mod.log.Info("Not replaying " + command + " from player " + fromPlayer + ": waiting for the fresh save");
                return;
            }

            m_Received++;
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
            m_ModeSetThisFrame = false;

            if (ApplyModeSetter == null)
            {
                return;
            }

            // Every other sync system stands down while the game is loading; this one did not, and that is
            // where the OverlapExisting errors came from. A city that is loading is about to be replaced by
            // the shared save, which already contains everything queued here, so replaying the queue into it
            // afterwards builds all of it a second time on top of itself. Drop the queue and start clean.
            GameManager manager = GameManager.instance;
            if (manager == null || manager.gameMode != GameMode.Game || manager.isGameLoading)
            {
                if (m_Queue.Count > 0)
                {
                    Mod.log.Info("Dropping " + m_Queue.Count + " queued build(s): the city is being replaced and the save already holds them");
                    m_Skipped += m_Queue.Count;
                    m_Queue.Clear();
                }

                DiscardInjected();
                m_Phase = Phase.Idle;
                m_Current = null;
                m_SavedTool = null;
                m_SavedPrefab = null;
                return;
            }

            m_FrameCount++;
            if (m_Received > 0 && m_FrameCount - m_LedgerLoggedAt >= LedgerEveryFrames)
            {
                m_LedgerLoggedAt = m_FrameCount;
                Mod.log.Info("Build ledger: " + Ledger());
            }

            Step();
            SwallowBorrowedClick();
        }

        /// <summary>
        /// While the selection tool is borrowed the player still sees their own tool in the toolbar, so a click
        /// now must not select whatever is under the cursor (it would open its panel and, applied together with
        /// the other player's build, get captured). It does nothing instead; the player's tool is back next frame.
        /// </summary>
        private void SwallowBorrowedClick()
        {
            if (m_SavedTool != null && !m_ModeSetThisFrame && m_ToolSystem.activeTool == m_DefaultTool && m_ToolSystem.applyMode == ApplyMode.Apply)
            {
                SetApplyMode(ApplyMode.None);
            }
        }

        private void Step()
        {
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

                    // A build that had nothing to attach to went to the back of the queue. If it is all that is
                    // left, pause before trying it again rather than burning every frame on it.
                    if (m_Queue.Count == 1 && m_Queue[0].AnchorWaits > 0 && m_FrameCount < m_AnchorRetryUntilFrame)
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
                            // The honest no. The game here refuses part of what the other player built, which
                            // means the two cities no longer agree about what is on the ground. Building the
                            // rest anyway left a half a road or a building without its approach, and that
                            // wreckage is what every later build in the area then collided with: one refusal
                            // turned into a cascade. Better to build none of it, say so, and let it be
                            // repaired, than to manufacture a difference nobody can see.
                            //
                            // It gets one more go first: the pieces this build attaches to may simply not
                            // have landed yet, and a second attempt a moment later usually finds them.
                            if (m_Current.AnchorWaits < AnchorRetryLimit)
                            {
                                if (m_Current.AnchorWaits == 0)
                                {
                                    m_Retried++;
                                    Mod.log.Info("Replay of " + m_Current.Command + " held: " + errors + " of " + temps + " parts are not valid here yet" + details);
                                }

                                m_Current.AnchorWaits++;
                                SetApplyMode(ApplyMode.Clear);
                                m_Queue.Add(m_Current);
                                m_AnchorRetryUntilFrame = m_FrameCount + AnchorRetryFrames;
                                DiscardInjected();
                                m_Phase = Phase.Idle;
                                m_Current = null;
                                return;
                            }

                            Mod.log.Warn("Replay of " + m_Current.Command + ": " + errors + " of " + temps + " parts are still not valid here; building none of it" + details);
                            m_Partial++;
                            m_Skipped++;
                            m_Failed++;
                            SetApplyMode(ApplyMode.Clear);
                            Report(false, errors + " of " + temps + " parts are not valid here, so none of it was built" + details);
                            NoteFailure();
                            Finish();
                            return;
                        }

                        Mod.log.Info("Replay of " + m_Current.Command + ": " + errors + " of " + temps + " temp entities carry a warning here" + details);
                    }

                    SyncGuard.IsReplaying = true;
                    SyncGuard.CaptureAnyway = m_Current.CaptureAnyway;
                    SetApplyMode(ApplyMode.Apply);
                    m_Phase = Phase.Applied;
                    return;
                }

                case Phase.Applied:
                    m_Replayed++;
                    m_BatchedTotal += m_Batched;
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
                m_HeldFrames = 0;
                return true;
            }

            if (m_ToolSystem.applyMode == ApplyMode.Apply)
            {
                // The player is placing something this very frame; let that land first.
                return false;
            }

            if (ApplyHeld(active) && ++m_HeldFrames < HeldButtonWaitFrames)
            {
                // Mouse button down: a road being dragged out, zones being painted. Taking the tool now would
                // cut that short and, with the button still down when it comes back, start a fresh one.
                return false;
            }

            if (++m_WaitedFrames < BorrowAfterFrames)
            {
                return false;
            }

            m_SavedTool = active;
            m_SavedPrefab = m_ToolSystem.activePrefab;
            SetActiveToolQuietly(m_DefaultTool);
            m_WaitedFrames = 0;
            m_HeldFrames = 0;
            return false;
        }

        private static bool ApplyHeld(ToolBaseSystem tool)
        {
            if (ApplyActionProperty == null)
            {
                return false;
            }

            try
            {
                return ApplyActionProperty.GetValue(tool) is IProxyAction action && action.enabled && action.IsPressed();
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>True for a definition entity this system made from another player's build (never the local player's own).</summary>
        public bool OwnsDefinition(Entity definition)
        {
            return m_Injected.Contains(definition);
        }

        private void RestoreTool()
        {
            if (m_SavedTool == null)
            {
                return;
            }

            try
            {
                if (m_ToolSystem.activeTool != m_DefaultTool)
                {
                    // The player picked something else while we had the selection tool; that choice went
                    // through the property and the UI already knows about it. Leave it.
                }
                else if (m_SavedTool.GetPrefab() == m_SavedPrefab)
                {
                    // The tool still holds its prefab: put it back the quiet way, so the UI, which never saw it
                    // go, has nothing to redraw.
                    SetActiveToolQuietly(m_SavedTool);
                }
                else if (m_SavedPrefab != null)
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
            DiscardInjected();
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
                // Nothing here could be recreated yet. The usual reason is that the thing this build attaches
                // to is a build of its own that has not landed on this PC yet, so the answer is to wait rather
                // than to throw the build away: dropping it is what starts a cascade, because everything the
                // other player builds on top of it fails too. The command goes back on the queue with a
                // deadline and only counts as a failure once that runs out.
                if (m_Current.AnchorWaits < AnchorRetryLimit)
                {
                    m_Current.AnchorWaits++;
                    if (m_Current.AnchorWaits == 1)
                    {
                        m_Retried++;
                        Mod.log.Info("Replay of " + m_Current.Command + " waits: nothing to attach to here yet" + (problems.Length > 0 ? " (" + problems + ")" : ""));
                    }

                    // To the back of the queue, not the front: whatever this build needs is most likely another
                    // build still on its way, so everything behind it should go first rather than be stalled.
                    m_Queue.Add(m_Current);
                    m_AnchorRetryUntilFrame = m_FrameCount + AnchorRetryFrames;
                    DiscardInjected();
                    m_Phase = Phase.Idle;
                    m_Current = null;
                    return;
                }

                Mod.log.Warn("Replay of " + m_Current.Command + " skipped after " + AnchorRetryLimit + " tries: nothing could be recreated here");
                NoteFailure();
                m_Skipped++;
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

        /// <summary>
        /// Destroys the definition entities this system created and forgets them. Every path that abandons a
        /// replay must come through here. Merely clearing the list left the definitions alive in the world with
        /// nothing owning them: the capture system's OwnsDefinition check reads the same list, so once it was
        /// cleared those definitions looked like the local player's own work and were broadcast back out. That
        /// is the "it is placing objects for my friend" fault, and the retry paths added tonight would have
        /// brought it back.
        /// </summary>
        private void DiscardInjected()
        {
            foreach (Entity definition in m_Injected)
            {
                if (EntityManager.Exists(definition))
                {
                    EntityManager.DestroyEntity(definition);
                }
            }

            m_Injected.Clear();
        }

        private void Finish()
        {
            // The generators consumed the definitions in the inject frame; nothing else cleans them up when
            // no tool owns them, and a later capture would pick stale ones up again.
            DiscardInjected();
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

        /// <summary>
        /// True only while the borrowed selection tool is the one actually running. If the player picked a
        /// tool of their own during a borrow, this goes false again and their builds are captured normally.
        /// </summary>
        public bool IsStandingInForPlayer => m_SavedTool != null && m_ToolSystem.activeTool == m_DefaultTool;

        /// <summary>
        /// Makes <paramref name="tool"/> the running tool without the tool-changed event. ToolUpdate still
        /// stops the old tool and starts the new one on its next pass, exactly as after the property, and the
        /// full-update flag is raised as the property would. Falls back to the property when the slot cannot
        /// be reached.
        /// </summary>
        private void SetActiveToolQuietly(ToolBaseSystem tool)
        {
            if (ActiveToolField == null || m_ToolSystem.activeTool == tool)
            {
                m_ToolSystem.activeTool = tool;
                return;
            }

            try
            {
                ActiveToolField.SetValue(m_ToolSystem, tool);
                m_ToolSystem.RequireFullUpdate();
            }
            catch (Exception ex)
            {
                Mod.log.Warn("Quiet tool switch failed (" + ex.Message + "); switching the loud way");
                m_ToolSystem.activeTool = tool;
            }
        }

        private void SetApplyMode(ApplyMode mode)
        {
            m_ModeSetThisFrame = true;
            ApplyModeSetter.Invoke(m_DefaultTool, new object[] { mode });
        }
    }
}
