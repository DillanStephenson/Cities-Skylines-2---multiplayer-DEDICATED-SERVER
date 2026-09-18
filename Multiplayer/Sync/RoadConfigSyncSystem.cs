using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game;
using Game.SceneFlow;
using Multiplayer.Core.Build;
using Multiplayer.Core.Session;
using Unity.Entities;

namespace Multiplayer.Sync
{
    /// <summary>
    /// Hands the Road Builder mod's custom roads round. Road Builder makes a new road prefab from a JSON
    /// configuration at runtime; a road built with it cannot be replayed on a PC that has not seen that
    /// configuration. So every configuration this game knows goes to the others when the session starts, when
    /// someone joins, and whenever a new one appears; the receiving side parses it with Road Builder's own
    /// loader and adds the prefab through Road Builder's own API. Everything is reached by reflection, so the
    /// mod builds and runs without Road Builder present.
    /// </summary>
    public partial class RoadConfigSyncSystem : GameSystemBase
    {
        private const int PassInterval = 30;
        private const int RetryTypesEvery = 300;

        private const string SystemType = "RoadBuilder.Systems.RoadBuilderSystem";
        private const string SaveUtilType = "RoadBuilder.Utilities.LocalSaveUtil";
        private const string JsonType = "Colossal.Json.JSON";
        private const string EncodeOptionsType = "Colossal.Json.EncodeOptions";

        private readonly List<ModConfigCommand> m_Incoming = new List<ModConfigCommand>();
        /// <summary>Road id to a hash of the JSON last seen for it. Road Builder lets a road be edited in
        /// place, keeping its id, so tracking ids alone meant an edited road was never sent again and the two
        /// PCs went on building with silently different geometry under the same name.</summary>
        private readonly Dictionary<string, int> m_Known = new Dictionary<string, int>(StringComparer.Ordinal);

        private static int HashJson(string json)
        {
            unchecked
            {
                int hash = 17;
                for (int i = 0; i < json.Length; i++)
                {
                    hash = hash * 31 + json[i];
                }

                return hash;
            }
        }
        private ComponentSystemBase m_RoadBuilder;
        private PropertyInfo m_Configurations;
        private PropertyInfo m_ConfigOfPrefab;
        private PropertyInfo m_ConfigName;
        private MethodInfo m_AddPrefab;
        private MethodInfo m_LoadFromJson;
        private MethodInfo m_Dump;
        private object m_EncodeOptions;
        private bool m_Bound;
        private bool m_ReportedMissing;
        private bool m_Primed;
        private bool m_ResendAll;
        private int m_Frame;
        private int m_Sent;
        private int m_Applied;

        public bool IsBound => m_Bound;

        public int SentCount => m_Sent;

        public int AppliedCount => m_Applied;

        public void Receive(ModConfigCommand command)
        {
            m_Incoming.Add(command);
        }

        /// <summary>Someone joined: they need every road we know.</summary>
        public void ResendAll()
        {
            m_ResendAll = true;
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            GameManager manager = GameManager.instance;
            if (service == null || service.Session.State != SessionState.Connected || manager == null || manager.gameMode != GameMode.Game || manager.isGameLoading)
            {
                m_Primed = false;
                m_Known.Clear();
                m_Incoming.Clear();
                return;
            }

            m_Frame++;
            if (!m_Bound && (m_Frame == 1 || m_Frame % RetryTypesEvery == 0))
            {
                Bind();
            }

            if (!m_Bound)
            {
                m_Incoming.Clear();
                return;
            }

            if (m_Incoming.Count > 0)
            {
                ApplyIncoming();
            }

            if (m_Frame % PassInterval != 0)
            {
                return;
            }

            try
            {
                Scan(service);
            }
            catch (Exception ex)
            {
                Mod.log.Warn("Road config sync: scanning failed: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ binding

        private void Bind()
        {
            Type system = MirrorFactory.FindType(SystemType);
            Type saveUtil = MirrorFactory.FindType(SaveUtilType);
            Type json = MirrorFactory.FindType(JsonType);
            Type encodeOptions = MirrorFactory.FindType(EncodeOptionsType);
            if (system == null || saveUtil == null)
            {
                if (!m_ReportedMissing)
                {
                    m_ReportedMissing = true;
                    Mod.log.Info("Road config sync: the Road Builder mod is not loaded here; skipped");
                }

                return;
            }

            try
            {
                m_RoadBuilder = World.GetExistingSystemManaged(system);
                m_Configurations = system.GetProperty("Configurations", BindingFlags.Instance | BindingFlags.Public);
                m_AddPrefab = system.GetMethod("AddPrefab", BindingFlags.Instance | BindingFlags.Public);
                m_LoadFromJson = saveUtil.GetMethod("LoadFromJson", BindingFlags.Static | BindingFlags.Public);
                Type builderPrefab = MirrorFactory.FindType("RoadBuilder.Domain.Prefabs.INetworkBuilderPrefab");
                Type config = MirrorFactory.FindType("RoadBuilder.Domain.Configurations.INetworkConfig");
                m_ConfigOfPrefab = builderPrefab != null ? builderPrefab.GetProperty("Config") : null;
                m_ConfigName = config != null ? config.GetProperty("Name") : null;
                if (json != null && encodeOptions != null)
                {
                    m_Dump = json.GetMethod("Dump", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(object), encodeOptions }, null);
                    m_EncodeOptions = Enum.ToObject(encodeOptions, 0);
                }

                if (m_RoadBuilder == null || m_Configurations == null || m_AddPrefab == null || m_LoadFromJson == null || m_ConfigOfPrefab == null || m_Dump == null)
                {
                    throw new InvalidOperationException("Road Builder's API looks different from the one this build knows");
                }

                m_Bound = true;
                Mod.log.Info("Road config sync: tracking Road Builder's roads");
            }
            catch (Exception ex)
            {
                m_ReportedMissing = true;
                Mod.log.Warn("Road config sync: cannot track Road Builder's roads: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ outgoing

        private void Scan(MultiplayerService service)
        {
            var configurations = m_Configurations.GetValue(m_RoadBuilder, null) as IDictionary;
            if (configurations == null)
            {
                return;
            }

            bool sendEverything = !m_Primed || m_ResendAll;
            m_Primed = true;
            m_ResendAll = false;
            foreach (DictionaryEntry entry in configurations)
            {
                string id = entry.Key as string;
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                object config = entry.Value != null ? m_ConfigOfPrefab.GetValue(entry.Value, null) : null;
                if (config == null)
                {
                    continue;
                }

                string json;
                try
                {
                    json = m_Dump.Invoke(null, new[] { config, m_EncodeOptions }) as string;
                }
                catch (Exception ex)
                {
                    Mod.log.Warn("Road config sync: cannot serialise road " + id + ": " + ex.Message);
                    continue;
                }

                if (string.IsNullOrEmpty(json) || json.Length > ModConfigCommand.MaxJsonLength)
                {
                    continue;
                }

                // Send when the road is new here, or when its shape has changed since it was last sent.
                int hash = HashJson(json);
                bool changed = !m_Known.TryGetValue(id, out int lastHash) || lastHash != hash;
                m_Known[id] = hash;
                if (!changed && !sendEverything)
                {
                    continue;
                }

                string name = m_ConfigName != null ? m_ConfigName.GetValue(config, null) as string : null;
                service.SendModConfig(new ModConfigCommand { Id = id, Name = name ?? id, Json = json });
                m_Sent++;
            }
        }

        // ------------------------------------------------------------------ incoming

        private void ApplyIncoming()
        {
            var batch = new List<ModConfigCommand>(m_Incoming);
            m_Incoming.Clear();
            var configurations = m_Configurations.GetValue(m_RoadBuilder, null) as IDictionary;
            foreach (ModConfigCommand command in batch)
            {
                if (string.IsNullOrEmpty(command.Id))
                {
                    continue;
                }

                // Skip only when this exact road, with this exact shape, is already here. An edited road keeps
                // its id, so comparing ids alone let an edit through unapplied.
                int incoming = HashJson(command.Json ?? string.Empty);
                if (m_Known.TryGetValue(command.Id, out int mine) && mine == incoming)
                {
                    continue;
                }

                if (!m_Known.ContainsKey(command.Id) && configurations != null && configurations.Contains(command.Id))
                {
                    // Present here already but never hashed (it was made locally): adopt it without rebuilding.
                    m_Known[command.Id] = incoming;
                    continue;
                }

                try
                {
                    object config = m_LoadFromJson.Invoke(null, new object[] { command.Json });
                    if (config == null)
                    {
                        Mod.log.Warn("Road config sync: Road Builder could not read " + command);
                        continue;
                    }

                    object added = m_AddPrefab.Invoke(m_RoadBuilder, new[] { config, (object)false });
                    m_Known[command.Id] = incoming;
                    if (added == null)
                    {
                        Mod.log.Warn("Road config sync: Road Builder did not add " + command + " (see its log)");
                        continue;
                    }

                    m_Applied++;
                    Mod.log.Info("Road config sync: added " + command);
                }
                catch (Exception ex)
                {
                    Mod.log.Warn("Road config sync: could not add " + command + ": " + (ex.InnerException ?? ex).Message);
                }
            }
        }
    }
}
