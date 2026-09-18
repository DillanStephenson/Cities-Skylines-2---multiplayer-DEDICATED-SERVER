using System;
using System.IO;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Colossal.PSI.Environment;
using Game;
using Game.Modding;
using Game.SceneFlow;

namespace Multiplayer
{
    /// <summary>
    /// Entry point the game calls. Wires settings, the session service and the per-frame networking system.
    /// The session itself lives in the server window (Multiplayer.Server.exe next to this DLL); every game
    /// instance, the host's included, is a client of it.
    /// </summary>
    public class Mod : IMod
    {
        public const string Name = "Multiplayer";

        public static ILog log = LogManager.GetLogger(Name).SetShowsErrorsInUI(false);

        public static Setting Settings { get; private set; }

        public static MultiplayerService Service { get; private set; }

        /// <summary>Folder the game loaded this mod from; the server executable ships in it.</summary>
        public static string ModDirectory { get; private set; } = string.Empty;

        /// <summary>Set when this mod is installed twice; shown on the Options page and in the in-game panel.</summary>
        public static string DuplicateWarning { get; private set; }

        /// <summary>
        /// The same mod installed twice over: once here, and once as the copy Paradox downloads for a
        /// subscriber. The game runs the code from one of them and the interface from the other, and the two
        /// then disagree — which is why the in-game panel and the syncing box can stop appearing while
        /// everything else still works. Nothing in the mod can pick for the player, so say so plainly.
        /// </summary>
        private static string FindDuplicateInstall(string modDirectory)
        {
            try
            {
                if (string.IsNullOrEmpty(modDirectory))
                {
                    return null;
                }

                string subscribed = Path.Combine(EnvPath.kUserDataPath, Path.Combine(".cache", Path.Combine("Mods", "pdx_mods")));
                if (!Directory.Exists(subscribed))
                {
                    return null;
                }

                foreach (string folder in Directory.GetDirectories(subscribed, Core.Protocol.ProtocolConstants.ParadoxModId + "_*"))
                {
                    if (string.Equals(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar),
                            Path.GetFullPath(modDirectory).TrimEnd(Path.DirectorySeparatorChar),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (File.Exists(Path.Combine(folder, "Multiplayer.dll")) || File.Exists(Path.Combine(folder, "Multiplayer.mjs")))
                    {
                        return "This mod is installed twice: here (" + modDirectory + ") and as a subscribed copy ("
                            + folder + "). The game mixes the two, which stops the in-game panel and the syncing box "
                            + "appearing. Unsubscribe from Multiplayer Co-op on Paradox Mods, or delete the folder above, "
                            + "and restart the game.";
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                log.Warn("Could not check for a second copy of the mod: " + ex.Message);
                return null;
            }
        }

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad) + " mod version " + Core.Protocol.ProtocolConstants.ModVersion);

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                log.Info("Mod asset at " + asset.path);
                ModDirectory = Path.GetDirectoryName(asset.path) ?? string.Empty;
            }

            DuplicateWarning = FindDuplicateInstall(ModDirectory);
            if (DuplicateWarning != null)
            {
                log.Warn(DuplicateWarning);
            }

            Settings = new Setting(this);
            Settings.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(Settings));
            AssetDatabase.global.LoadSettings(Name, Settings, new Setting(this));

            Service = new MultiplayerService(Settings, log, ModDirectory);
            log.Info("Game version " + Game.Version.current.shortVersion + ", protocol " + Core.Protocol.ProtocolConstants.ProtocolVersion
                + ", server executable " + (File.Exists(Path.Combine(ModDirectory, MultiplayerService.ServerExecutable)) ? "present" : "MISSING"));

            // MainLoop runs every frame, in the menu and in-game, so the socket pump never stalls.
            updateSystem.UpdateAt<NetworkingSystem>(SystemUpdatePhase.MainLoop);

            // Build sync: capture right before the game's apply system realises a click; replay right before
            // the output system decides whether to run the apply phase this frame.
            updateSystem.UpdateBefore<Sync.BuildCaptureSystem, Game.Tools.ToolApplySystem>(SystemUpdatePhase.ApplyTool);
            updateSystem.UpdateBefore<Sync.BuildReplaySystem, Game.Tools.ToolOutputSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<Sync.CityStateSyncSystem>(SystemUpdatePhase.MainLoop);
            updateSystem.UpdateBefore<Sync.PolicySyncSystem, Game.Policies.ModifiedSystem>(SystemUpdatePhase.Modification4);

            // Other mods' per-entity settings (Traffic Tool Essentials junctions, for one): same phase their panels write in.
            updateSystem.UpdateAt<Sync.ModDataSyncSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<Sync.TrafficLaneSyncSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<Sync.RoadConfigSyncSystem>(SystemUpdatePhase.UIUpdate);

            // Main-menu entry and Join / Host screens (Multiplayer.mjs next to this DLL talks to these bindings).
            updateSystem.UpdateAt<MultiplayerUISystem>(SystemUpdatePhase.UIUpdate);

            // Where everyone is, and what their tool is showing: sent a few times a second, drawn with the tool overlay renderer.
            updateSystem.UpdateAt<Sync.PresenceSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<Sync.PreviewCaptureSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<Sync.PresenceRenderSystem>(SystemUpdatePhase.Rendering);

            DevTriggers.Apply(Service, Settings);
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));

            Service?.Shutdown();
            Service = null;

            if (Settings != null)
            {
                Settings.UnregisterInOptionsUI();
                Settings = null;
            }
        }
    }
}
