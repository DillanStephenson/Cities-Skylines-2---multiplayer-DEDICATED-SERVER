using System;
using Game;
using Game.Prefabs;
using Game.Tools;
using Multiplayer.Core.Build;
using Multiplayer.Core.Session;
using Unity.Collections;
using Unity.Entities;

namespace Multiplayer.Sync
{
    /// <summary>
    /// Runs in the ApplyTool phase, right before the game's ToolApplySystem, which means exactly when the
    /// local player's click is being realised. The definition entities that produced the temps being applied
    /// are still alive at that point (the tool's replacement of them is queued behind the same barrier), so
    /// they are read, serialised and sent to the server as one build command.
    /// </summary>
    public partial class BuildCaptureSystem : GameSystemBase
    {
        private ToolSystem m_ToolSystem;
        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_DefinitionQuery;
        private DefinitionCodec m_Codec;
        private int m_Sequence;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_DefinitionQuery = GetEntityQuery(ComponentType.ReadOnly<CreationDefinition>());
            m_Codec = new DefinitionCodec(EntityManager, new EntityResolver(World, m_PrefabSystem));
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || service.Session.State != SessionState.Connected)
            {
                return;
            }

            if (SyncGuard.IsReplaying && !SyncGuard.CaptureAnyway)
            {
                return;
            }

            if (m_DefinitionQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            var command = new BuildCommand { Sequence = ++m_Sequence, ToolId = m_ToolSystem.activeTool != null ? m_ToolSystem.activeTool.toolID : "?" };
            int skippedSelect = 0;
            using (NativeArray<Entity> definitions = m_DefinitionQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < definitions.Length; i++)
                {
                    try
                    {
                        CreationDefinition creation = EntityManager.GetComponentData<CreationDefinition>(definitions[i]);
                        // Select: the default tool picking an entity. Permanent: the simulation spawning sub-parts
                        // (driveways, building sub-nets) through the same pipeline; those happen on every game by themselves.
                        if ((creation.m_Flags & (CreationFlags.Select | CreationFlags.Permanent)) != 0)
                        {
                            skippedSelect++;
                            continue;
                        }

                        command.Definitions.Add(m_Codec.Read(definitions[i]));
                    }
                    catch (Exception ex)
                    {
                        Mod.log.Warn("Could not capture a definition: " + ex);
                    }
                }
            }

            if (command.Definitions.Count == 0)
            {
                return;
            }

            service.SendBuild(command);
        }
    }
}
