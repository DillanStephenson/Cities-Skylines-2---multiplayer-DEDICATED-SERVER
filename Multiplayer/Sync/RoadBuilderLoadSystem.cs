using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Serialization.Entities;
using Game;
using Game.Prefabs;
using Game.SceneFlow;

namespace Multiplayer.Sync
{
    /// <summary>
    /// Saves the player a click when the shared city carries Road Builder roads this PC has never seen.
    ///
    /// Road Builder stores each custom road inside the save. While a save is read in it creates any road it did
    /// not know yet, but the road pieces in the city were read before their road type existed, so it asks the
    /// player to reload the same save straight away. In a shared city that happens to everyone who joins after
    /// someone makes a new road, and it is easy to miss or to answer late.
    ///
    /// For a shared city this mod is loading, this takes Road Builder's list of new roads at the moment the save
    /// has been read in (before Road Builder would show its message), finishes those roads exactly the way Road
    /// Builder does, and then reloads the city once, from the file already on disk, behind the syncing box. If
    /// Road Builder is missing or has changed shape, nothing here happens and its own message appears as before.
    /// </summary>
    public partial class RoadBuilderLoadSystem : GameSystemBase
    {
        private const string SerializeSystemType = "RoadBuilder.Systems.RoadBuilderSerializeSystem";
        private const string RoadBuilderSystemType = "RoadBuilder.Systems.RoadBuilderSystem";
        private const string BuilderPrefabType = "RoadBuilder.Domain.Prefabs.INetworkBuilderPrefab";

        private readonly List<object> m_Taken = new List<object>();
        private bool m_BindTried;
        private bool m_Bound;
        private FieldInfo m_NewRoadsField;
        private PropertyInfo m_PrefabProperty;
        private MethodInfo m_UpdatePrefab;
        private Type m_RoadBuilderSystem;

        protected override void OnUpdate()
        {
        }

        private bool Bind()
        {
            if (m_BindTried)
            {
                return m_Bound;
            }

            m_BindTried = true;
            try
            {
                Type serialize = MirrorFactory.FindType(SerializeSystemType);
                m_RoadBuilderSystem = MirrorFactory.FindType(RoadBuilderSystemType);
                Type builderPrefab = MirrorFactory.FindType(BuilderPrefabType);
                if (serialize == null || m_RoadBuilderSystem == null || builderPrefab == null)
                {
                    return false;
                }

                m_NewRoadsField = serialize.GetField("_prefabsToUpdate", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                m_PrefabProperty = builderPrefab.GetProperty("Prefab");
                m_UpdatePrefab = m_RoadBuilderSystem.GetMethod("UpdatePrefab", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(NetGeometryPrefab) }, null);
                m_Bound = m_NewRoadsField != null && m_PrefabProperty != null && m_UpdatePrefab != null;
                if (!m_Bound)
                {
                    Mod.log.Warn("Road Builder is installed but not in the shape expected; its reload message will appear as usual");
                }
            }
            catch (Exception ex)
            {
                Mod.log.Warn("Could not look inside Road Builder: " + ex.Message);
                m_Bound = false;
            }

            return m_Bound;
        }

        /// <summary>Right after the save has been read in: every road Road Builder did not know is in its list now.</summary>
        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            m_Taken.Clear();

            MultiplayerService service = Mod.Service;
            if (service == null || !service.WorldSync.IsLoadingSharedCity || service.WorldSync.RoadReloadDone)
            {
                return;
            }

            if (!Bind())
            {
                return;
            }

            try
            {
                if (!(m_NewRoadsField.GetValue(null) is IList newRoads) || newRoads.Count == 0)
                {
                    return;
                }

                foreach (object road in newRoads)
                {
                    m_Taken.Add(road);
                }

                // With its list empty, Road Builder has nothing to report and shows no message.
                newRoads.Clear();
                Mod.log.Info("Road Builder: " + m_Taken.Count + " custom road(s) in the shared city are new on this PC; the city will load once more by itself");
            }
            catch (Exception ex)
            {
                Mod.log.Warn("Could not take over Road Builder's new roads (" + ex.Message + "); its reload message will appear as usual");
                m_Taken.Clear();
            }
        }

        /// <summary>Finishes the new roads the way Road Builder would, then asks for the one reload.</summary>
        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            if (m_Taken.Count == 0)
            {
                return;
            }

            if (mode != GameMode.Game)
            {
                m_Taken.Clear();
                return;
            }

            object roadBuilder = m_RoadBuilderSystem != null ? World.GetExistingSystemManaged(m_RoadBuilderSystem) : null;
            int finished = 0;
            foreach (object road in m_Taken)
            {
                try
                {
                    if (roadBuilder != null && m_PrefabProperty.GetValue(road) is NetGeometryPrefab prefab)
                    {
                        m_UpdatePrefab.Invoke(roadBuilder, new object[] { prefab });
                        finished++;
                    }
                }
                catch (Exception ex)
                {
                    Mod.log.Warn("Road Builder could not finish a new road: " + ex.Message);
                }
            }

            m_Taken.Clear();
            try
            {
                // The one other thing Road Builder does here: new roads bring new names.
                GameManager.instance.localizationManager.ReloadActiveLocale();
            }
            catch (Exception ex)
            {
                Mod.log.Warn("Could not refresh road names: " + ex.Message);
            }

            Mod.log.Info("Road Builder: finished " + finished + " new road(s); reloading the city once so they are built from them");
            Mod.Service?.WorldSync.RequestRoadReload();
        }
    }
}
