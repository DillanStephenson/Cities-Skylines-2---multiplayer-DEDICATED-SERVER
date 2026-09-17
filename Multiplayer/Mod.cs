using System.IO;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
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

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad) + " mod version " + Core.Protocol.ProtocolConstants.ModVersion);

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                log.Info("Mod asset at " + asset.path);
                ModDirectory = Path.GetDirectoryName(asset.path) ?? string.Empty;
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

            // Main-menu entry and Join / Host screens (Multiplayer.mjs next to this DLL talks to these bindings).
            updateSystem.UpdateAt<MultiplayerUISystem>(SystemUpdatePhase.UIUpdate);

            // Where everyone is: sent after the camera settles, drawn with the tool overlay renderer.
            updateSystem.UpdateAt<Sync.PresenceSystem>(SystemUpdatePhase.UIUpdate);
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
