using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Threading;
using Multiplayer.Core.Session;

namespace Multiplayer.Server
{
    /// <summary>
    /// Follows a published Paradox playset: asks the public Paradox Mods API every few minutes whether the
    /// playset has a new public version and, when it does, reads its full mod list. Runs on its own thread;
    /// the main loop takes finished snapshots with <see cref="TryTake"/> so the session is only touched
    /// from one thread.
    /// </summary>
    public sealed class PlaysetWatcher : IDisposable
    {
        private static readonly HttpClient Http = CreateClient();

        private readonly int _playsetId;
        private readonly int _pollMinutes;
        private readonly ISessionLog _log;
        private readonly ConcurrentQueue<PlaysetSnapshot> _ready = new ConcurrentQueue<PlaysetSnapshot>();
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private Thread _thread;
        private int _lastVersion = -1;
        private string _lastUpdated = string.Empty;
        private DateTime _lastFailureLogged = DateTime.MinValue;

        public PlaysetWatcher(int playsetId, int pollMinutes, ISessionLog log)
        {
            _playsetId = playsetId;
            _pollMinutes = Math.Max(1, pollMinutes);
            _log = log;
        }

        public int PlaysetId => _playsetId;

        public void Start()
        {
            if (_thread != null)
            {
                return;
            }

            _thread = new Thread(Run) { IsBackground = true, Name = "playset-watch" };
            _thread.Start();
        }

        /// <summary>A newly read playset, if one arrived since the last call.</summary>
        public bool TryTake(out PlaysetSnapshot snapshot)
        {
            return _ready.TryDequeue(out snapshot);
        }

        /// <summary>Forget what was seen so the next poll re-reads the playset even if unchanged.</summary>
        public void Refresh()
        {
            _lastVersion = -1;
            _lastUpdated = string.Empty;
        }

        public void Dispose()
        {
            _stop.Set();
        }

        private void Run()
        {
            while (true)
            {
                try
                {
                    Poll();
                }
                catch (Exception ex)
                {
                    if ((DateTime.UtcNow - _lastFailureLogged).TotalMinutes >= 10)
                    {
                        _lastFailureLogged = DateTime.UtcNow;
                        _log.Warn("Could not read Paradox playset " + _playsetId + ": " + ex.Message + " (keeping the last known list)");
                    }
                }

                if (_stop.WaitOne(TimeSpan.FromMinutes(_pollMinutes)))
                {
                    return;
                }
            }
        }

        private void Poll()
        {
            string details = Get(ParadoxPlayset.DetailsUrl(_playsetId));
            PlaysetSnapshot snapshot = ParadoxPlayset.ParseDetails(details, _playsetId);
            if (snapshot.Version == _lastVersion && string.Equals(snapshot.Updated, _lastUpdated, StringComparison.Ordinal))
            {
                return;
            }

            int page = 1;
            while (page <= 50)
            {
                int total = ParadoxPlayset.AddMods(snapshot, Get(ParadoxPlayset.ModsUrl(_playsetId, page, snapshot.Version)));
                if (snapshot.Mods.Count >= total || snapshot.Mods.Count >= snapshot.ModsCount && snapshot.ModsCount > 0)
                {
                    break;
                }

                page++;
            }

            _lastVersion = snapshot.Version;
            _lastUpdated = snapshot.Updated;
            _ready.Enqueue(snapshot);
        }

        private static string Get(string url)
        {
            using (HttpResponseMessage response = Http.GetAsync(url).GetAwaiter().GetResult())
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException("HTTP " + (int)response.StatusCode + " from " + url);
                }

                return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
        }

        private static HttpClient CreateClient()
        {
#if NET48
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
#endif
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CS2-Multiplayer-Server/" + Multiplayer.Core.Protocol.ProtocolConstants.ModVersion);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return client;
        }
    }
}
