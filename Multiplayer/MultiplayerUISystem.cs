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
        private int m_Frame;

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
            if (++m_Frame % RefreshFrames != 0)
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
