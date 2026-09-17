using System;
using System.Text;
using Colossal.UI.Binding;
using Game;
using Game.SceneFlow;
using Game.UI;
using Game.UI.Menu;
using Multiplayer.Core.Session;

namespace Multiplayer
{
    /// <summary>
    /// Bindings behind the mod's own screens: the main-menu entry and its Join / Host forms with the session
    /// status. Values are pushed a few times a second; triggers call straight into the service. The screen
    /// is the game's credits sub-screen with our content swapped in while <c>screenActive</c> is true, which
    /// is the one sub-screen the menu lets a mod open without touching the menu's own navigation code.
    /// </summary>
    public partial class MultiplayerUISystem : UISystemBase
    {
        public const string Group = "multiplayer";
        private const int RefreshFrames = 10;

        private ValueBinding<bool> m_ScreenActive;
        private ValueBinding<string> m_PlayerName;
        private ValueBinding<string> m_HostPort;
        private ValueBinding<string> m_HostPassword;
        private ValueBinding<string> m_JoinAddress;
        private ValueBinding<string> m_JoinPassword;
        private ValueBinding<string> m_JoinOwnerKey;
        private ValueBinding<string> m_State;
        private ValueBinding<bool> m_Online;
        private ValueBinding<bool> m_IsOwner;
        private ValueBinding<bool> m_InGame;
        private ValueBinding<string> m_ServerName;
        private ValueBinding<string> m_Players;
        private ValueBinding<string> m_World;
        private ValueBinding<string> m_WorldStatus;
        private ValueBinding<string> m_LastError;
        private ValueBinding<string> m_Recent;
        private ValueBinding<bool> m_NewerCity;
        private ValueBinding<string> m_RequestedView;
        private ValueBinding<bool> m_TransferBusy;
        private ValueBinding<string> m_Presence;
        private readonly StringBuilder m_PresenceJson = new StringBuilder();
        private string m_PendingView;
        private int m_PendingFrames;
        private int m_Frame;

        /// <summary>Frames to let the main menu settle before a requested screen is opened (about ten seconds).</summary>
        private const int OpenDelayFrames = 600;

        /// <summary>
        /// Open a screen without a click: "choice", "join" or "host" open the menu screen on that view once
        /// the main menu is up; "panel" opens the in-game panel. Used by the dev trigger for screenshots.
        /// </summary>
        public void RequestView(string view)
        {
            m_PendingView = (view ?? string.Empty).Trim().ToLowerInvariant();
            m_PendingFrames = 0;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            Setting settings = Mod.Settings;

            AddBinding(m_ScreenActive = new ValueBinding<bool>(Group, "screenActive", false));
            AddBinding(m_PlayerName = new ValueBinding<string>(Group, "playerName", settings != null ? settings.PlayerName ?? string.Empty : string.Empty));
            AddBinding(m_HostPort = new ValueBinding<string>(Group, "hostPort", settings != null ? settings.HostPort ?? string.Empty : string.Empty));
            AddBinding(m_HostPassword = new ValueBinding<string>(Group, "hostPassword", settings != null ? settings.HostPassword ?? string.Empty : string.Empty));
            AddBinding(m_JoinAddress = new ValueBinding<string>(Group, "joinAddress", settings != null ? settings.JoinAddress ?? string.Empty : string.Empty));
            AddBinding(m_JoinPassword = new ValueBinding<string>(Group, "joinPassword", settings != null ? settings.JoinPassword ?? string.Empty : string.Empty));
            AddBinding(m_JoinOwnerKey = new ValueBinding<string>(Group, "joinOwnerKey", settings != null ? settings.JoinOwnerKey ?? string.Empty : string.Empty));

            AddBinding(m_State = new ValueBinding<string>(Group, "state", "Offline"));
            AddBinding(m_Online = new ValueBinding<bool>(Group, "online", false));
            AddBinding(m_IsOwner = new ValueBinding<bool>(Group, "isOwner", false));
            AddBinding(m_InGame = new ValueBinding<bool>(Group, "inGame", false));
            AddBinding(m_ServerName = new ValueBinding<string>(Group, "serverName", string.Empty));
            AddBinding(m_Players = new ValueBinding<string>(Group, "players", string.Empty));
            AddBinding(m_World = new ValueBinding<string>(Group, "world", string.Empty));
            AddBinding(m_WorldStatus = new ValueBinding<string>(Group, "worldStatus", string.Empty));
            AddBinding(m_LastError = new ValueBinding<string>(Group, "lastError", string.Empty));
            AddBinding(m_Recent = new ValueBinding<string>(Group, "recent", string.Empty));
            AddBinding(m_NewerCity = new ValueBinding<bool>(Group, "newerCity", false));
            AddBinding(m_RequestedView = new ValueBinding<string>(Group, "requestedView", string.Empty));
            AddBinding(m_TransferBusy = new ValueBinding<bool>(Group, "transferBusy", false));
            AddBinding(m_Presence = new ValueBinding<string>(Group, "presence", string.Empty));

            AddBinding(new TriggerBinding<string>(Group, "setPlayerName", value => Store(m_PlayerName, value, s => s.PlayerName = value)));
            AddBinding(new TriggerBinding<string>(Group, "setHostPort", value => Store(m_HostPort, value, s => s.HostPort = value)));
            AddBinding(new TriggerBinding<string>(Group, "setHostPassword", value => Store(m_HostPassword, value, s => s.HostPassword = value)));
            AddBinding(new TriggerBinding<string>(Group, "setJoinAddress", value => Store(m_JoinAddress, value, s => s.JoinAddress = value)));
            AddBinding(new TriggerBinding<string>(Group, "setJoinPassword", value => Store(m_JoinPassword, value, s => s.JoinPassword = value)));
            AddBinding(new TriggerBinding<string>(Group, "setJoinOwnerKey", value => Store(m_JoinOwnerKey, value, s => s.JoinOwnerKey = value)));

            AddBinding(new TriggerBinding(Group, "host", () => Mod.Service?.HostGame()));
            AddBinding(new TriggerBinding(Group, "join", () => Mod.Service?.JoinGame()));
            AddBinding(new TriggerBinding(Group, "leave", () => Mod.Service?.Leave()));
            AddBinding(new TriggerBinding(Group, "uploadCity", () => Mod.Service?.UploadCityNow()));
            AddBinding(new TriggerBinding(Group, "fetchCity", () => Mod.Service?.FetchCityNow()));
            AddBinding(new TriggerBinding(Group, "openScreen", OpenScreen));
            AddBinding(new TriggerBinding(Group, "screenExited", () => m_ScreenActive.Update(false)));
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();
            if (!string.IsNullOrEmpty(m_PendingView))
            {
                ServePendingView();
            }

            if ((++m_Frame & 1) == 0)
            {
                m_Presence.Update(BuildPresenceJson());
            }

            if (m_Frame % RefreshFrames != 0)
            {
                return;
            }

            GameManager manager = GameManager.instance;
            m_InGame.Update(manager != null && manager.gameMode == GameMode.Game);

            MultiplayerService service = Mod.Service;
            if (service == null)
            {
                m_State.Update("Offline");
                m_Online.Update(false);
                return;
            }

            ClientSession session = service.Session;
            m_State.Update(service.IsWaitingForSpawnedServer ? "Starting the server window" : Describe(session.State));
            m_Online.Update(service.IsOnline);
            m_IsOwner.Update(session.IsOwner);
            m_ServerName.Update(session.ServerName ?? string.Empty);

            var players = new StringBuilder();
            foreach (PlayerInfo player in session.Players)
            {
                if (players.Length > 0)
                {
                    players.Append('\n');
                }

                players.Append(player.Name);
                if (player.IsOwner)
                {
                    players.Append(" (owner)");
                }

                if (player.PlayerId == session.LocalPlayerId)
                {
                    players.Append(" (you)");
                }
            }

            m_Players.Update(players.ToString());
            m_World.Update(session.ServerWorld != null ? session.ServerWorld.ToString() : session.ServerWorldKnown ? "No city on the server yet" : string.Empty);
            m_WorldStatus.Update(service.WorldSync.StatusLine ?? string.Empty);
            m_LastError.Update(session.State == SessionState.Failed ? session.LastError ?? string.Empty : string.Empty);
            m_Recent.Update(string.Join("\n", service.RecentLines));
            m_NewerCity.Update(session.ServerWorld != null && session.ServerWorld.Revision != service.WorldSync.LoadedRevision);
            m_TransferBusy.Update(service.WorldSync.IsBusy);
        }

        /// <summary>Screen positions for the other players' name tags: [{"id":2,"n":"Bob","x":312,"y":540,"off":false,"c":"#ff9e33"}].</summary>
        private string BuildPresenceJson()
        {
            MultiplayerService service = Mod.Service;
            GameManager manager = GameManager.instance;
            if (service == null || service.Session.State != SessionState.Connected || manager == null || manager.gameMode != GameMode.Game || manager.isGameLoading || Sync.PresenceStore.Count == 0)
            {
                return string.Empty;
            }

            UnityEngine.Camera camera = UnityEngine.Camera.main;
            if (camera == null)
            {
                return string.Empty;
            }

            const float margin = 48f;
            float width = UnityEngine.Screen.width;
            float height = UnityEngine.Screen.height;
            m_PresenceJson.Length = 0;
            m_PresenceJson.Append('[');
            bool first = true;
            foreach (Sync.RemotePresence player in Sync.PresenceStore.Snapshot(service.NowMs))
            {
                UnityEngine.Vector3 screen = camera.WorldToScreenPoint(new UnityEngine.Vector3(player.Pivot.x, player.Pivot.y + 2f, player.Pivot.z));
                bool behind = screen.z < 0f;
                float x = behind ? width - screen.x : screen.x;
                float y = behind ? screen.y : height - screen.y;
                bool off = behind || x < margin || x > width - margin || y < margin || y > height - margin;
                x = Math.Min(Math.Max(x, margin), width - margin);
                y = Math.Min(Math.Max(y, margin), height - margin);

                if (!first)
                {
                    m_PresenceJson.Append(',');
                }

                first = false;
                m_PresenceJson.Append("{\"id\":").Append(player.PlayerId)
                    .Append(",\"n\":\"").Append(JsonEscape(player.Name)).Append('"')
                    .Append(",\"x\":").Append((int)x)
                    .Append(",\"y\":").Append((int)y)
                    .Append(",\"off\":").Append(off ? "true" : "false")
                    .Append(",\"c\":\"").Append(Sync.PresenceStore.HexFor(player.PlayerId)).Append("\"}");
            }

            m_PresenceJson.Append(']');
            return first ? string.Empty : m_PresenceJson.ToString();
        }

        private static string JsonEscape(string text)
        {
            var builder = new StringBuilder(text.Length + 4);
            foreach (char c in text)
            {
                if (c == '"' || c == '\\')
                {
                    builder.Append('\\').Append(c);
                }
                else if (c < ' ')
                {
                    builder.Append(' ');
                }
                else
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }

        private void ServePendingView()
        {
            GameManager manager = GameManager.instance;
            if (manager == null || manager.isGameLoading)
            {
                return;
            }

            if (m_PendingView == "panel")
            {
                if (manager.gameMode != GameMode.Game)
                {
                    return;
                }

                m_RequestedView.Update("panel");
                m_PendingView = null;
                return;
            }

            if (manager.gameMode != GameMode.MainMenu)
            {
                return;
            }

            if (++m_PendingFrames < OpenDelayFrames)
            {
                return;
            }

            m_RequestedView.Update(m_PendingView == "join" || m_PendingView == "host" ? m_PendingView : "choice");
            m_PendingView = null;
            OpenScreen();
        }

        private static string Describe(SessionState state)
        {
            switch (state)
            {
                case SessionState.Offline: return "Offline";
                case SessionState.Connecting: return "Connecting";
                case SessionState.Handshaking: return "Joining";
                case SessionState.Connected: return "Connected";
                case SessionState.Failed: return "Failed";
                default: return state.ToString();
            }
        }

        private static void Store(ValueBinding<string> binding, string value, Action<Setting> assign)
        {
            value = value ?? string.Empty;
            Setting settings = Mod.Settings;
            if (settings != null)
            {
                assign(settings);
                try
                {
                    settings.ApplyAndSave();
                }
                catch (Exception ex)
                {
                    Mod.log.Warn("Could not save settings: " + ex.Message);
                }
            }

            binding.Update(value);
        }

        private void OpenScreen()
        {
            MenuUISystem menu = World.GetExistingSystemManaged<MenuUISystem>();
            if (menu == null)
            {
                Mod.log.Warn("Menu UI system missing; cannot open the multiplayer screen");
                return;
            }

            m_ScreenActive.Update(true);
            menu.activeScreen = MenuUISystem.MenuScreen.Credits;
        }
    }
}
