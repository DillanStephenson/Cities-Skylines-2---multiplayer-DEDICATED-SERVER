using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using Game.UI.Widgets;
using Multiplayer.Core.Protocol;
using System.Collections.Generic;

namespace Multiplayer
{
    /// <summary>
    /// Options > Multiplayer page. Milestone 1 UI: host (spawns the server window) or join by address.
    /// A proper main-menu panel comes later; this page is enough to exercise the session end to end.
    /// </summary>
    [FileLocation("ModsSettings/" + Mod.Name + "/" + Mod.Name)]
    [SettingsUIGroupOrder(kPlayerGroup, kHostGroup, kJoinGroup, kSessionGroup)]
    [SettingsUIShowGroupName(kPlayerGroup, kHostGroup, kJoinGroup, kSessionGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";

        public const string kPlayerGroup = "Player";
        public const string kHostGroup = "Host";
        public const string kJoinGroup = "Join";
        public const string kSessionGroup = "Session";

        public Setting(IMod mod) : base(mod)
        {
            SetDefaults();
        }

        // ---------------------------------------------------------------- player

        [SettingsUITextInput]
        [SettingsUISection(kSection, kPlayerGroup)]
        public string PlayerName { get; set; }

        // ---------------------------------------------------------------- host

        [SettingsUITextInput]
        [SettingsUISection(kSection, kHostGroup)]
        public string HostPort { get; set; }

        [SettingsUITextInput]
        [SettingsUISection(kSection, kHostGroup)]
        public string HostPassword { get; set; }

        [SettingsUIButton]
        [SettingsUISection(kSection, kHostGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsOnline))]
        public bool HostGame
        {
            set { Mod.Service?.HostGame(); }
        }

        // ---------------------------------------------------------------- join

        [SettingsUITextInput]
        [SettingsUISection(kSection, kJoinGroup)]
        public string JoinAddress { get; set; }

        [SettingsUITextInput]
        [SettingsUISection(kSection, kJoinGroup)]
        public string JoinPassword { get; set; }

        [SettingsUITextInput]
        [SettingsUISection(kSection, kJoinGroup)]
        public string JoinOwnerKey { get; set; }

        [SettingsUIButton]
        [SettingsUISection(kSection, kJoinGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsOnline))]
        public bool JoinGame
        {
            set { Mod.Service?.JoinGame(); }
        }

        // ---------------------------------------------------------------- session

        [SettingsUIButton]
        [SettingsUISection(kSection, kSessionGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsOnline), true)]
        public bool Disconnect
        {
            set { Mod.Service?.Leave(); }
        }

        [SettingsUIButton]
        [SettingsUISection(kSection, kSessionGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsOnline), true)]
        public bool UploadCity
        {
            set { Mod.Service?.UploadCityNow(); }
        }

        [SettingsUIButton]
        [SettingsUISection(kSection, kSessionGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsOnline), true)]
        public bool FetchCity
        {
            set { Mod.Service?.FetchCityNow(); }
        }

        [SettingsUISlider(min = 0, max = 30, step = 5, scalarMultiplier = 1)]
        [SettingsUISection(kSection, kSessionGroup)]
        public int AutoUploadMinutes { get; set; }

        /// <summary>No longer used: a plain save never reloads the others (that lost everything built since). Kept so old settings files still load.</summary>
        [SettingsUIHidden]
        public bool FollowSaves { get; set; }

        [SettingsUISlider(min = 0, max = 60, step = 5, scalarMultiplier = 1)]
        [SettingsUISection(kSection, kSessionGroup)]
        public int SyncEveryMinutes { get; set; }

        /// <summary>Read-only text block; the settings UI renders getter-only strings with this attribute as multiline text.</summary>
        [SettingsUIMultilineText]
        [SettingsUISection(kSection, kSessionGroup)]
        public string Status => Mod.Service != null ? Mod.Service.StatusText : "Offline";

        public bool IsOnline()
        {
            return Mod.Service != null && Mod.Service.IsOnline;
        }

        public override void SetDefaults()
        {
            PlayerName = "Mayor";
            HostPort = ProtocolConstants.DefaultPort.ToString();
            HostPassword = string.Empty;
            JoinAddress = "127.0.0.1:" + ProtocolConstants.DefaultPort;
            JoinPassword = string.Empty;
            JoinOwnerKey = string.Empty;
            AutoUploadMinutes = 5;
            FollowSaves = false;
            SyncEveryMinutes = 0;
        }
    }

    public class LocaleEN : IDictionarySource
    {
        private readonly Setting m_Setting;

        public LocaleEN(Setting setting)
        {
            m_Setting = setting;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "Multiplayer" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "Main" },

                { m_Setting.GetOptionGroupLocaleID(Setting.kPlayerGroup), "Player" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kHostGroup), "Host a session" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kJoinGroup), "Join a session" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kSessionGroup), "Session" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.PlayerName)), "Player name" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.PlayerName)), "How other players see you. Read when you host or join." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.HostPort)), "Port" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.HostPort)), "TCP port the server window listens on. Friends outside your network need this port forwarded to your PC." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.HostPassword)), "Password" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.HostPassword)), "Leave empty for an open session." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.HostGame)), "Host" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.HostGame)), "Opens the server window next to the game and joins it as the host. The window shows who is connected and takes admin commands." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.JoinAddress)), "Server address" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.JoinAddress)), "address or address:port of the server window, for example 203.0.113.5:27015." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.JoinPassword)), "Password" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.JoinPassword)), "Password the host set, if any." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.JoinOwnerKey)), "Owner key (optional)" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.JoinOwnerKey)), "Only when joining a server window you started by hand: the key it printed, which makes you the host." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.JoinGame)), "Join" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.JoinGame)), "Connect to the server." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Disconnect)), "Disconnect" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Disconnect)), "Leave the current session. If you are hosting, the server window closes shortly after." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.UploadCity)), "Upload my city" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.UploadCity)), "Host only: save the city you are playing and make it the shared city on the server. Happens by itself when the server has none." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.FetchCity)), "Get the shared city" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.FetchCity)), "Download the server's city and load it. Happens by itself from the main menu or when you join for the first time." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AutoUploadMinutes)), "Auto-upload every (minutes)" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AutoUploadMinutes)), "How often the city is saved to the server on its own while you play. The host does it, or the longest-connected player when no host is on. 0 turns it off." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.SyncEveryMinutes)), "Sync everyone every (minutes)" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.SyncEveryMinutes)), "Host only. Every so many minutes everyone stops behind a Syncing world box, your city goes up to the server and the others load it, so nobody drifts apart for long. 0 turns it off. The panel has a button for doing it right now." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.FollowSaves)), "Reload when someone else saves" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.FollowSaves)), "When another player saves the city to the server, download it and reload straight away (about a minute of loading). Off: the panel tells you and you fetch it when you like." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.Status)), "Status" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.Status)), "Connection state, players and recent events." },
            };
        }

        public void Unload()
        {
        }
    }
}
