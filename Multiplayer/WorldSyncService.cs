using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Colossal;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Colossal.PSI.Common;
using Colossal.PSI.Environment;
using Game;
using Game.Assets;
using Game.SceneFlow;
using Game.UI;
using Game.UI.Menu;
using Multiplayer.Core.Build;
using Multiplayer.Core.Session;
using Unity.Entities;

namespace Multiplayer
{
    /// <summary>
    /// Moves the city between this game and the server window.
    /// Upload (owner): save through the game's own save path into the user's save folder, read the package
    /// back, stream it to the server. Download (anyone): write the package into the save folder, register it
    /// with the asset database, then load it exactly the way the Load Game menu would.
    /// Rules: the server's copy is the shared city. A player at the main menu, or a guest who has not loaded
    /// it yet, gets it automatically; someone already in a city is told and can fetch it with a button.
    /// The owner uploads when the server has nothing, on demand, and every few minutes while in sync.
    /// </summary>
    public sealed class WorldSyncService
    {
        private const string SaveNamePrefix = "MP ";
        private const int RetryDelayMs = 30000;
        private const int RegisterWaitFrames = 600;
        private const int NewCityGraceMs = 10000;
        private const long RecentBuildMs = 120000;
        private const int MinAutoUploadMinutes = 5;

        /// <summary>When the local player last placed or removed something (service clock); 0 when never.</summary>
        public long LastLocalBuildMs { get; set; }

        /// <summary>
        /// Set when this game found it can no longer follow the others' builds (several could not be recreated
        /// here): the next save from the source of truth is loaded as soon as it arrives, whatever the settings.
        /// The source of truth is the leader (the host, else the longest-connected player), who never resyncs.
        /// </summary>
        public bool ResyncPending { get; private set; }

        private long _lastAskedUploadMs = -1;
        private const long AskedUploadMinGapMs = 30000;

        /// <summary>
        /// Lets go of the builds that arrived while replays were held. They are discarded when a fresh save has
        /// just loaded (it already contains them) and replayed when the hold simply ended.
        /// </summary>
        private static void ReleaseHeldBuilds(bool alreadyInTheSave)
        {
            try
            {
                World world = World.DefaultGameObjectInjectionWorld;
                world?.GetExistingSystemManaged<Sync.BuildReplaySystem>()?.ReleaseHeld(alreadyInTheSave);
            }
            catch (Exception ex)
            {
                _staticLog?.Warn("Could not release held builds: " + ex.Message);
            }
        }

        private static ILog _staticLog;

        // ---------------------------------------------------------------- Road Builder reload

        /// <summary>True between this mod starting a load of the shared city and that load completing.</summary>
        public bool IsLoadingSharedCity => _awaitingLoadRevision != 0;

        /// <summary>
        /// True once the automatic reload for new Road Builder roads has happened for the city currently loaded,
        /// so it can never loop. Cleared whenever a freshly downloaded city starts loading.
        /// </summary>
        public bool RoadReloadDone { get; private set; }

        /// <summary>
        /// True while that reload is under way. It reloads the very save that is already running, so builds that
        /// arrived since are not in it and must be kept for replay rather than dropped, which is what happens to
        /// them when a newer save is loaded.
        /// </summary>
        public bool ReloadingSameSave { get; private set; }

        private SaveInfo _loadedSaveInfo;
        private bool _roadReloadRequested;

        /// <summary>Called when new Road Builder roads were finished during a load: reload the same city once.</summary>
        public void RequestRoadReload()
        {
            _roadReloadRequested = true;
        }

        private void RunRoadReloadIfDue()
        {
            if (!_roadReloadRequested)
            {
                return;
            }

            GameManager manager = GameManager.instance;
            if (manager == null || manager.gameMode != GameMode.Game || manager.isGameLoading || _busy)
            {
                return;
            }

            _roadReloadRequested = false;
            if (_loadedSaveInfo == null || RoadReloadDone)
            {
                return;
            }

            RoadReloadDone = true;
            ReloadingSameSave = true;
            _busy = true;
            _busyWhat = "loading the custom roads";
            _awaitingLoadRevision = _loadedRevision != 0 ? _loadedRevision : 1;
            SyncModalText = "This city has custom roads that were new on this PC. Loading it once more so they come out right.";
            _note("Custom roads new to this PC: loading the city once more, which Road Builder needs");
            try
            {
                LoadThroughMenu(_loadedSaveInfo);
            }
            catch (Exception ex)
            {
                _log.Warn("Could not reload the city for its custom roads: " + ex.Message);
                _note("Could not reload the city by itself; reload it from the panel if roads look wrong");
                ReloadingSameSave = false;
                SyncModalText = string.Empty;
                _awaitingLoadRevision = 0;
                Idle();
            }
        }

        /// <summary>A resync that never arrives must not silence this game forever; see <see cref="ResyncWaitLimitMs"/>.</summary>
        private const long ResyncWaitLimitMs = 120000;
        private long _resyncSinceMs = -1;

        public void RequestResync()
        {
            if (_session.IsLeader || ResyncPending)
            {
                return;
            }

            ResyncPending = true;
            _resyncSinceMs = NowMs();
            RefreshStatus();
        }

        /// <summary>
        /// While a resync is pending this game throws away every build the others send, because a fresh save
        /// is supposed to be on its way. If that save never comes (nobody is in a city to make it, the leader
        /// left, an upload failed), the game would go on silently discarding builds for the rest of the
        /// session and drift further with every one. After two minutes, give up waiting and start replaying
        /// again: a city that is a little out of step is better than one that stopped listening.
        /// </summary>
        private void ExpireResync(long nowMs)
        {
            if (!ResyncPending || _resyncSinceMs < 0 || nowMs - _resyncSinceMs < ResyncWaitLimitMs)
            {
                return;
            }

            ResyncPending = false;
            _resyncSinceMs = -1;
            SyncModalText = string.Empty;
            ReleaseHeldBuilds(false);
            _note("No fresh save arrived within two minutes; carrying on with the city as it is");
            RefreshStatus();
        }

        // ---------------------------------------------------------------- forced group sync

        private const long ForcedSyncWaitLimitMs = 150000;
        private long _lastForcedSyncMs = -1;
        private long _forcedSyncSinceMs = -1;
        private bool _forcedSyncUpload;
        private bool _forcedSyncWaiting;

        /// <summary>Text of the full-screen "Syncing world" box while a forced sync is under way; empty when hidden.</summary>
        public string SyncModalText { get; private set; } = string.Empty;

        /// <summary>True while other players' builds must not be replayed here: a fresh save is on its way.</summary>
        public bool HoldReplays => ResyncPending || _forcedSyncWaiting;

        /// <summary>
        /// Leader only: everyone stops behind a sync box, this game saves and uploads, and once the server has
        /// it everyone else loads that revision. Scheduled by the setting or pressed in the panel.
        /// </summary>
        public bool ForceSyncNow(long nowMs)
        {
            GameManager manager = GameManager.instance;
            if (!_session.IsLeader || _session.State != SessionState.Connected || _busy || manager == null || manager.gameMode != GameMode.Game || manager.isGameLoading)
            {
                return false;
            }

            _lastForcedSyncMs = nowMs;
            _forcedSyncUpload = true;
            SyncModalText = "Saving the shared city and sending it to everyone. Hold on.";
            _sendSync(new WorldSyncCommand { Phase = WorldSyncPhase.Start });
            StartUpload(nowMs, "forced sync of everyone");
            return true;
        }

        private Action<WorldSyncCommand> _sendSync = command => { };

        /// <summary>How the service sends world sync notices; set once by the service.</summary>
        public void SetSyncSender(Action<WorldSyncCommand> sender)
        {
            _sendSync = sender ?? (command => { });
        }

        /// <summary>A forced sync notice from the leader.</summary>
        public void OnWorldSync(WorldSyncCommand command, long nowMs)
        {
            if (_session.IsLeader)
            {
                return;
            }

            switch (command.Phase)
            {
                case WorldSyncPhase.Start:
                    _forcedSyncWaiting = true;
                    _forcedSyncSinceMs = nowMs;
                    SyncModalText = "The host is saving the shared city. It loads here as soon as it arrives.";
                    _note("Syncing world: waiting for the host's save");
                    break;

                case WorldSyncPhase.Ready:
                    _forcedSyncWaiting = false;
                    if (command.Revision != 0 && command.Revision == _loadedRevision)
                    {
                        SyncModalText = string.Empty;
                        ReleaseHeldBuilds(false);
                        _note("Syncing world: already on revision " + command.Revision);
                        break;
                    }

                    SyncModalText = "Loading the shared city, revision " + command.Revision + ".";
                    ResyncPending = true;
                    _resyncSinceMs = nowMs;
                    _nextAttemptMs = 0;
                    break;

                case WorldSyncPhase.Cancel:
                    _forcedSyncWaiting = false;
                    SyncModalText = string.Empty;
                    ReleaseHeldBuilds(false);
                    _note("Syncing world: the host's save did not happen; carrying on");
                    break;
            }

            RefreshStatus();
        }

        private void UpdateForcedSync(long nowMs, bool inGame)
        {
            if (_forcedSyncWaiting && nowMs - _forcedSyncSinceMs > ForcedSyncWaitLimitMs)
            {
                _forcedSyncWaiting = false;
                SyncModalText = string.Empty;
                ReleaseHeldBuilds(false);
                _note("Syncing world: no save arrived from the host; carrying on");
            }

            int minutes = _settings.SyncEveryMinutes;
            if (minutes > 0 && _session.IsLeader && inGame && _loadedRevision != 0 && !_busy)
            {
                long since = _lastForcedSyncMs >= 0 ? _lastForcedSyncMs : _lastUploadMs;
                if (since >= 0 && nowMs - since > minutes * 60000L)
                {
                    ForceSyncNow(nowMs);
                }
                else if (since < 0)
                {
                    _lastForcedSyncMs = nowMs;
                }
            }
        }

        /// <summary>The leader was asked for a fresh save by someone who fell out of step: upload now, at most every 30 s.</summary>
        public void UploadIfAsked(long nowMs, string why, bool evenIfNotLeader = false)
        {
            GameManager manager = GameManager.instance;
            if ((!_session.IsLeader && !evenIfNotLeader) || _busy || manager == null || manager.gameMode != GameMode.Game || manager.isGameLoading)
            {
                return;
            }

            if (_lastAskedUploadMs >= 0 && nowMs - _lastAskedUploadMs < AskedUploadMinGapMs)
            {
                return;
            }

            _lastAskedUploadMs = nowMs;
            StartUpload(nowMs, why);
        }

        // ---------------------------------------------------------------- fresh save on join

        /// <summary>How long a joiner waits for the players in the city to save before taking the stored copy.</summary>
        private const long FreshSaveWaitMs = 75000;
        private long _freshSaveAskedMs = -1;
        private int _freshSaveBaseRevision;

        /// <summary>
        /// The copy on the server is as old as the last save; the players in the city have built since. Before
        /// the first download of a connection, when someone else is on, ask them to save now and wait (up to
        /// <see cref="FreshSaveWaitMs"/>) for a newer revision to appear. True while still waiting.
        /// </summary>
        private bool WaitForFreshSave(long nowMs, WorldInfo serverWorld)
        {
            if (serverWorld == null || _session.Players.Count <= 1)
            {
                return false;
            }

            if (_freshSaveAskedMs < 0)
            {
                _freshSaveAskedMs = nowMs;
                _freshSaveBaseRevision = serverWorld.Revision;
                try
                {
                    _session.SendGameplayCommand(MultiplayerService.SaveNowKind, new byte[0]);
                }
                catch (Exception ex)
                {
                    _log.Warn("Could not ask for a fresh save: " + ex.Message);
                    return false;
                }

                _note("Asking the players in the city to save it first (up to a minute)...");
                RefreshStatus();
                return true;
            }

            if (serverWorld.Revision > _freshSaveBaseRevision)
            {
                _note("Fresh save arrived: revision " + serverWorld.Revision);
                return false;
            }

            if (nowMs - _freshSaveAskedMs < FreshSaveWaitMs)
            {
                return true;
            }

            _note("Nobody saved within the minute; fetching the stored copy (revision " + serverWorld.Revision + ")");
            return false;
        }
        private const int NewCityMaxTries = 5;

        private long _newCityInGameSinceMs = -1;
        private int _newCityTries;

        private readonly ClientSession _session;
        private readonly Setting _settings;
        private readonly ILog _log;
        private readonly Action<string> _note;

        private int _loadedRevision;
        private bool _busy;
        private string _busyWhat = string.Empty;
        private long _nextAttemptMs;
        private long _lastUploadMs = -1;
        private long _downloadReceived;
        private long _downloadTotal;
        private int _awaitingLoadRevision;
        private bool _bootstrapped;

        public string StatusLine { get; private set; } = string.Empty;

        /// <summary>True while a save-and-upload or a download-and-load is in flight.</summary>
        public bool IsBusy => _busy;

        /// <summary>Revision of the server world this game is running, 0 when it is not.</summary>
        public int LoadedRevision => _loadedRevision;

        /// <summary>
        /// Set when the host connected from the menu to start a fresh city: the server's current city is not fetched,
        /// and the next city this game loads is uploaded to replace it.
        /// </summary>
        public bool NewCityPending { get; set; }

        /// <summary>
        /// Set while the host, connected from the main menu to a server that already holds a city, has not said
        /// whether to load that city or start a new world. Nothing is downloaded until they answer.
        /// </summary>
        public bool HostChoicePending { get; private set; }

        private bool _hostChoiceAsked;

        /// <summary>The host answered "load the server's city": the normal download follows on the next update.</summary>
        public void ChooseLoad()
        {
            HostChoicePending = false;
            RefreshStatus();
        }

        /// <summary>The host answered "start a new world": the next city they start or load replaces the server's.</summary>
        public void ChooseNewWorld()
        {
            HostChoicePending = false;
            NewCityPending = true;
            RefreshStatus();
        }

        public WorldSyncService(ClientSession session, Setting settings, ILog log, Action<string> note)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _staticLog = _log;
            _note = note ?? (_ => { });

            _session.StateChanged += OnStateChanged;
            _session.WorldInfoReceived += _ => RefreshStatus();
            _session.WorldDownloadProgress += (got, total) =>
            {
                _downloadReceived = got;
                _downloadTotal = total;
                RefreshStatus();
            };
            _session.WorldDownloaded += OnWorldDownloaded;
            _session.WorldDownloadFailed += reason =>
            {
                _note("World download failed: " + reason);
                Idle();
            };
            _session.WorldUploadFinished += OnUploadFinished;
        }

        // ---------------------------------------------------------------- per-frame

        public void Update(long nowMs)
        {
            // Callbacks that fire outside Update (an upload finishing, a load completing) stamp their time
            // from here, so that every stamp and every comparison uses one clock.
            _lastUpdateMs = nowMs;
            ExpireResync(nowMs);
            RunRoadReloadIfDue();

            if (!_bootstrapped)
            {
                _bootstrapped = true;
                GameManager gameManager = GameManager.instance;
                if (gameManager != null)
                {
                    gameManager.onGameLoadingComplete += OnGameLoadingComplete;
                }
            }

            GameManager currentManager = GameManager.instance;
            UpdateForcedSync(nowMs, currentManager != null && currentManager.gameMode == GameMode.Game && !currentManager.isGameLoading);

            if (_session.State != SessionState.Connected || _busy || !_session.ServerWorldKnown || nowMs < _nextAttemptMs)
            {
                return;
            }

            GameManager manager = GameManager.instance;
            if (manager == null || manager.isGameLoading)
            {
                return;
            }

            bool inGame = manager.gameMode == GameMode.Game;
            bool inMenu = manager.gameMode == GameMode.MainMenu;
            WorldInfo serverWorld = _session.ServerWorld;

            if (NewCityPending)
            {
                if (!inGame)
                {
                    // Still in the menu, or loading: the server's city is deliberately not fetched.
                    _newCityInGameSinceMs = -1;
                    return;
                }

                if (_newCityInGameSinceMs < 0)
                {
                    _newCityInGameSinceMs = nowMs;
                    return;
                }

                if (nowMs - _newCityInGameSinceMs < NewCityGraceMs)
                {
                    // A city that has just started cannot be saved for a few seconds (the game's save info is not ready).
                    return;
                }

                if (_newCityTries >= NewCityMaxTries)
                {
                    NewCityPending = false;
                    _newCityTries = 0;
                    _note("New city: the upload kept failing; press Save to server once the city runs");
                    return;
                }

                _newCityTries++;
                StartUpload(nowMs, "new city for the server");
                return;
            }

            if (serverWorld != null && serverWorld.Revision != _loadedRevision)
            {
                // The host is asked first: load what the server holds, or start a new world. Everyone else just loads it.
                if (inMenu && _session.IsOwner && _loadedRevision == 0 && !_hostChoiceAsked)
                {
                    _hostChoiceAsked = true;
                    HostChoicePending = true;
                    RefreshStatus();
                    return;
                }

                if (HostChoicePending)
                {
                    if (!inGame)
                    {
                        return;
                    }

                    // They went into a city of their own instead of answering; the question is moot.
                    HostChoicePending = false;
                }

                // From the menu: always. In a city: when this game has not loaded the shared city yet (unless it
                // is the one expected to upload its own), or when the player asked to follow other people's saves.
                // Following saves never interrupts someone who is building: what they placed in the last two
                // minutes may not be in that save yet, and a reload would take it away from under them.
                // A plain save by someone else never reloads a running game: the others already have every
                // build live, and a reload would throw away whatever was built after that save started. Only
                // the group sync and a drift resync (both set ResyncPending) reload.
                bool automatic = inMenu
                    || (inGame && _loadedRevision == 0 && !_session.IsLeader)
                    || (inGame && _loadedRevision != 0 && ResyncPending);
                if (!automatic)
                {
                    return;
                }

                if (inMenu && !ResyncPending && WaitForFreshSave(nowMs, serverWorld))
                {
                    return;
                }

                StartDownload(nowMs);
                return;
            }

            if (_session.IsLeader && inGame)
            {
                if (serverWorld == null)
                {
                    StartUpload(nowMs, "the server has no city yet");
                    return;
                }

                // Never more often than every five minutes. A save of a big modded city takes seconds and makes
                // the game stutter while it runs; until tonight this timer never fired at all (a clock mix-up),
                // so a setting of one minute that used to do nothing would now stall the host every minute.
                int minutes = _settings.AutoUploadMinutes > 0 ? Math.Max(MinAutoUploadMinutes, _settings.AutoUploadMinutes) : 0;
                if (minutes > 0 && _loadedRevision != 0 && _lastUploadMs >= 0 && nowMs - _lastUploadMs > minutes * 60000L)
                {
                    StartUpload(nowMs, "periodic backup");
                }
            }
        }

        // ---------------------------------------------------------------- buttons

        public void UploadNow(long nowMs)
        {
            if (_session.State != SessionState.Connected)
            {
                _note("Not connected");
                return;
            }

            GameManager manager = GameManager.instance;
            if (manager == null || manager.gameMode != GameMode.Game || manager.isGameLoading)
            {
                _note("Load a city first, then upload it");
                return;
            }

            if (_busy)
            {
                _note("Busy: " + _busyWhat);
                return;
            }

            StartUpload(nowMs, "requested");
        }

        public void DownloadNow(long nowMs)
        {
            if (_session.State != SessionState.Connected || _session.ServerWorld == null)
            {
                _note("The server holds no city to fetch");
                return;
            }

            if (_busy)
            {
                _note("Busy: " + _busyWhat);
                return;
            }

            // Fetching the server's city on purpose ends any "new city" or "load or new?" state: what loads is the shared city.
            NewCityPending = false;
            HostChoicePending = false;
            StartDownload(nowMs);
        }

        // ---------------------------------------------------------------- upload

        private void StartUpload(long nowMs, string why)
        {
            _busy = true;
            _busyWhat = "saving and uploading the city";
            _nextAttemptMs = nowMs + RetryDelayMs;
            _note("Uploading the city (" + why + ")...");
            RefreshStatus();

            // The game serialises every save and load on its "SaveLoadGame" task queue (its own autosave, the
            // menu, loading). Saving outside that queue can overlap the game's autosave and crash the serializer
            // with a null reference, so the upload goes through the same queue.
            try
            {
                TaskManager.instance.EnqueueTask("SaveLoadGame", UploadTask, 1);
            }
            catch (Exception ex)
            {
                _log.Warn("Could not queue the save with the game (" + ex.Message + "); saving directly");
                UploadTask();
            }
        }

        private async Task UploadTask()
        {
            try
            {
                World world = World.DefaultGameObjectInjectionWorld;
                MenuUISystem menu = world.GetExistingSystemManaged<MenuUISystem>();
                SaveInfo info = menu.GetSaveInfo(autoSave: false);
                string saveName = SaveNameFor(_session.ServerName);
                ILocalAssetDatabase database = AssetDatabase.user;
                AssetDataPath path = SaveHelpers.GetAssetDataPath<SaveGameMetadata>(database, saveName);

                // Save straight over the existing slot, exactly as the game's own Save and Quick Save do
                // (MenuUISystem.SaveGame and QuickSave never delete first). The slot is usually the very save
                // this city was loaded from, and deleting it first pulled that package out from under the
                // running game while it still held its data file open: the save then failed with "file in
                // use" part way through and left the serializer broken, which surfaced as a
                // NullReferenceException in SerializerSystem / EntityManager.HighestEntityIndex. Both reported
                // crashes during a save to the server were this.
                await GameManager.instance.Save(saveName, info, database, (ScreenCaptureHelper.AsyncRequest)null);

                if (!database.Exists<PackageAsset>(path, out PackageAsset package))
                {
                    throw new InvalidOperationException("The save package was not found after saving");
                }

                string file = ResolvePath(package.path);
                byte[] bytes = await Task.Run(() => File.ReadAllBytes(file));
                string guid = package.id.guid.ToString();
                _log.Info("Uploading " + file + " (" + WorldInfo.FormatSize(bytes.Length) + ", guid " + guid + ")");

                if (!_session.UploadWorld(saveName, info.cityName ?? string.Empty, guid, bytes))
                {
                    _note("Upload could not start (not connected as host?)");
                    Idle();
                }
            }
            catch (Exception ex)
            {
                _log.Error("Upload failed: " + ex);
                _note("Upload failed: " + ex.Message);
                if (_forcedSyncUpload)
                {
                    _forcedSyncUpload = false;
                    SyncModalText = string.Empty;
                    _sendSync(new WorldSyncCommand { Phase = WorldSyncPhase.Cancel });
                }
                Idle();
            }
        }

        private void OnUploadFinished(bool ok, int revision, string reason)
        {
            if (ok)
            {
                _loadedRevision = revision;
                _lastUploadMs = NowMs();
                NewCityPending = false;
                _newCityTries = 0;
                _note("City uploaded as revision " + revision);
                if (_forcedSyncUpload)
                {
                    _forcedSyncUpload = false;
                    SyncModalText = string.Empty;
                    _sendSync(new WorldSyncCommand { Phase = WorldSyncPhase.Ready, Revision = revision });
                    _note("Syncing world: everyone loads revision " + revision);
                }
            }
            else
            {
                _note("Upload rejected: " + reason);
                if (_forcedSyncUpload)
                {
                    _forcedSyncUpload = false;
                    SyncModalText = string.Empty;
                    _sendSync(new WorldSyncCommand { Phase = WorldSyncPhase.Cancel });
                }
            }

            Idle();
        }

        // ---------------------------------------------------------------- download + load

        private void StartDownload(long nowMs)
        {
            _busy = true;
            _busyWhat = "downloading the city";
            _nextAttemptMs = nowMs + RetryDelayMs;
            _downloadReceived = 0;
            _downloadTotal = _session.ServerWorld != null ? _session.ServerWorld.Size : 0;
            _note("Downloading " + _session.ServerWorld + "...");
            RefreshStatus();
            if (!_session.RequestWorld())
            {
                _note("Download could not start");
                Idle();
            }
        }

        private void OnWorldDownloaded(WorldSnapshot snapshot)
        {
            _busyWhat = "installing and loading the city";
            RefreshStatus();
            RunLoad(snapshot);
        }

        private async void RunLoad(WorldSnapshot snapshot)
        {
            try
            {
                string saveName = SanitizeSaveName(snapshot.Info.SaveName);
                if (saveName.Length == 0)
                {
                    saveName = SaveNameFor(_session.ServerName);
                }

                ILocalAssetDatabase database = AssetDatabase.user;
                AssetDataPath assetPath = SaveHelpers.GetAssetDataPath<SaveGameMetadata>(database, saveName);
                if (database.Exists<PackageAsset>(assetPath, out PackageAsset previous))
                {
                    database.DeleteAsset(previous);
                }

                string relativeDirectory = "Saves/" + PlatformManager.instance.userSpecificPath;
                string directory = Path.Combine(EnvPath.kUserDataPath, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(directory);
                string file = Path.Combine(directory, saveName + PackageAsset.kExtension);
                byte[] data = snapshot.Data;
                string guid = snapshot.Info.Guid ?? string.Empty;
                await Task.Run(() =>
                {
                    File.WriteAllBytes(file, data);
                    if (guid.Length > 0)
                    {
                        File.WriteAllText(file + AssetData.kMetaExtension, guid);
                    }
                });
                _log.Info("Wrote " + file + " (" + WorldInfo.FormatSize(data.Length) + ")");

                if (database.dataSource is FileSystemDataSource fileSystem)
                {
                    fileSystem.AddEntry(AssetDataPath.Create(relativeDirectory, saveName + PackageAsset.kExtension, hasExtension: true, EscapeStrategy.None), typeof(PackageAsset));
                }
                else
                {
                    _log.Warn("User database is not file based; relying on its own watcher to notice the save");
                }

                SaveGameMetadata metadata = null;
                for (int i = 0; i < RegisterWaitFrames && metadata == null; i++)
                {
                    metadata = FindSave(saveName);
                    if (metadata == null)
                    {
                        await Task.Delay(16);
                    }
                }

                if (metadata == null)
                {
                    throw new InvalidOperationException("The game did not register the downloaded save '" + saveName + "'");
                }

                SaveInfo info = metadata.target;
                if (info == null)
                {
                    throw new InvalidOperationException("Downloaded save has no metadata");
                }

                _awaitingLoadRevision = snapshot.Info.Revision;
                _loadedSaveInfo = info;
                RoadReloadDone = false;
                ReloadingSameSave = false;
                _note("Loading " + (string.IsNullOrEmpty(info.cityName) ? saveName : info.cityName) + " (revision " + snapshot.Info.Revision + ")...");
                LoadThroughMenu(info);
            }
            catch (Exception ex)
            {
                _log.Error("Installing the downloaded city failed: " + ex);
                _note("Could not load the downloaded city: " + ex.Message);
                _awaitingLoadRevision = 0;
                Idle();
            }
        }

        /// <summary>Same path the Load Game menu takes: refresh its save list, then hand it the id to load.</summary>
        private static void LoadThroughMenu(SaveInfo info)
        {
            World world = World.DefaultGameObjectInjectionWorld;
            MenuUISystem menu = world.GetExistingSystemManaged<MenuUISystem>();
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

            MethodInfo updateSaves = typeof(MenuUISystem).GetMethod("UpdateSaves", flags, null, Type.EmptyTypes, null);
            updateSaves?.Invoke(menu, null);

            MethodInfo load = typeof(MenuUISystem).GetMethod("SafeLoadGame", flags);
            if (load == null)
            {
                throw new MissingMethodException("MenuUISystem.SafeLoadGame not found; the game changed");
            }

            var args = new MenuUISystem.LoadGameArgs
            {
                saveId = info.id,
                cityName = info.cityName,
                options = info.options,
                gameMode = info.gameMode,
            };
            load.Invoke(menu, new object[] { args, true });
        }

        private void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            if (_awaitingLoadRevision == 0)
            {
                return;
            }

            if (mode == GameMode.Game)
            {
                _loadedRevision = _awaitingLoadRevision;
                _lastUploadMs = NowMs();
                ResyncPending = false;
                _resyncSinceMs = -1;
                SyncModalText = string.Empty;
                // A newer city that just loaded already contains everything held during the wait; a reload of the
                // same save for Road Builder does not, so those are replayed instead.
                ReleaseHeldBuilds(!ReloadingSameSave);
                _note("Now playing the shared city, revision " + _loadedRevision);
            }
            else
            {
                SyncModalText = string.Empty;
                // Nothing loaded, so what was held is still wanted here.
                ReleaseHeldBuilds(false);
                _note("The shared city did not load (game went to " + mode + ")");
            }

            _awaitingLoadRevision = 0;
            ReloadingSameSave = false;
            Idle();
        }

        // ---------------------------------------------------------------- helpers

        private static SaveGameMetadata FindSave(string saveName)
        {
            foreach (SaveGameMetadata metadata in AssetDatabase.global.GetAssets(default(SearchFilter<SaveGameMetadata>)))
            {
                try
                {
                    if (string.Equals(metadata.name, saveName, StringComparison.OrdinalIgnoreCase) && metadata.isValidSaveGame)
                    {
                        return metadata;
                    }
                }
                catch (Exception)
                {
                    // A half-registered asset; try again next frame.
                }
            }

            return null;
        }

        private static string ResolvePath(string path)
        {
            if (File.Exists(path))
            {
                return path;
            }

            string combined = Path.Combine(EnvPath.kUserDataPath, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(combined))
            {
                return combined;
            }

            throw new FileNotFoundException("Save package not found", path);
        }

        public static string SaveNameFor(string serverName)
        {
            string name = SanitizeSaveName(SaveNamePrefix + (serverName ?? string.Empty));
            return name.Length > SaveNamePrefix.Length ? name : SaveNamePrefix + "Server";
        }

        public static string SanitizeSaveName(string name)
        {
            var builder = new StringBuilder();
            foreach (char c in (name ?? string.Empty).Trim())
            {
                if (char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_')
                {
                    builder.Append(c);
                }
            }

            string result = builder.ToString().Trim();
            return result.Length > 40 ? result.Substring(0, 40).Trim() : result;
        }

        private void OnStateChanged(SessionState state)
        {
            if (state != SessionState.Connected)
            {
                _loadedRevision = 0;
                _lastUploadMs = -1;
                _awaitingLoadRevision = 0;
                _nextAttemptMs = 0;
                if (state == SessionState.Offline || state == SessionState.Failed)
                {
                    // Set before connecting, so it must survive the Connecting and Handshaking steps.
                    NewCityPending = false;
                }

                HostChoicePending = false;
                _hostChoiceAsked = false;
                ResyncPending = false;
                _resyncSinceMs = -1;
                _lastAskedUploadMs = -1;
                _freshSaveAskedMs = -1;
                _forcedSyncWaiting = false;
                _forcedSyncUpload = false;
                _lastForcedSyncMs = -1;
                SyncModalText = string.Empty;

                Idle();
            }
        }

        private void Idle()
        {
            _busy = false;
            _busyWhat = string.Empty;
            _downloadReceived = 0;
            _downloadTotal = 0;
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            if (_session.State != SessionState.Connected)
            {
                StatusLine = string.Empty;
                return;
            }

            var builder = new StringBuilder();
            if (_busy)
            {
                builder.Append("Working: ").Append(_busyWhat);
                if (_downloadTotal > 0)
                {
                    builder.Append(' ').Append(100 * _downloadReceived / _downloadTotal).Append('%');
                }
            }
            else if (HostChoicePending)
            {
                builder.Append("The server holds ").Append(_session.ServerWorld).Append(": load it, or start a new world?");
            }
            else if (ResyncPending)
            {
                builder.Append("Out of step with the shared city; the next save from the host loads on its own");
            }
            else if (NewCityPending)
            {
                builder.Append("New city: pick a map under New Game; the city you start goes to the server once it has loaded");
            }
            else if (!_session.ServerWorldKnown)
            {
                builder.Append("Shared city: unknown yet");
            }
            else if (_session.ServerWorld == null)
            {
                builder.Append(_session.IsLeader ? "Shared city: none yet; load a city and it uploads" : "Shared city: none yet; waiting for someone to save one");
            }
            else
            {
                WorldInfo world = _session.ServerWorld;
                builder.Append("Shared city: ").Append(world);
                builder.Append(world.Revision == _loadedRevision ? " (loaded)" : _loadedRevision == 0 ? " (not loaded)" : " (you run revision " + _loadedRevision + ")");
            }

            StatusLine = builder.ToString();
        }

        /// <summary>
        /// The clock everything in this class is compared against: the same one <see cref="Update"/> is driven
        /// with (MultiplayerService's stopwatch, counting from mod load). It used to be Environment.TickCount,
        /// which counts from when the PC booted, so stamps written here were millions of milliseconds ahead of
        /// the "now" they were later compared to. Every such comparison was false forever, which silently
        /// switched off the periodic save to the server AND the forced group sync.
        /// </summary>
        private long NowMs()
        {
            return _lastUpdateMs;
        }

        private long _lastUpdateMs;
    }
}
