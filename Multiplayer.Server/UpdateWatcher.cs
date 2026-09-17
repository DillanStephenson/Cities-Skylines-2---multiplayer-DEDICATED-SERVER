using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Threading;
using Multiplayer.Core.Session;

namespace Multiplayer.Server
{
    /// <summary>
    /// Asks GitHub for the newest release of the project at start and then every few hours. The main loop
    /// takes the answer with <see cref="TryTake"/>; the console says whether this server is behind, and the
    /// session tells the host and any player whose mod is newer than the server.
    /// </summary>
    public sealed class UpdateWatcher : IDisposable
    {
        private static readonly HttpClient Http = CreateClient();

        private readonly string _repository;
        private readonly int _pollHours;
        private readonly ISessionLog _log;
        private readonly ConcurrentQueue<ReleaseInfo> _ready = new ConcurrentQueue<ReleaseInfo>();
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private Thread _thread;
        private string _lastTag = string.Empty;
        private DateTime _lastFailureLogged = DateTime.MinValue;

        public UpdateWatcher(string repository, int pollHours, ISessionLog log)
        {
            _repository = repository;
            _pollHours = Math.Max(1, pollHours);
            _log = log;
        }

        public void Start()
        {
            if (_thread != null)
            {
                return;
            }

            _thread = new Thread(Run) { IsBackground = true, Name = "update-check" };
            _thread.Start();
        }

        public bool TryTake(out ReleaseInfo release)
        {
            return _ready.TryDequeue(out release);
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
                    string json;
                    using (HttpResponseMessage response = Http.GetAsync(GitHubReleases.LatestUrl(_repository)).GetAwaiter().GetResult())
                    {
                        if (response.StatusCode == HttpStatusCode.NotFound)
                        {
                            throw new HttpRequestException("no releases published yet (or the repository is private)");
                        }

                        if (!response.IsSuccessStatusCode)
                        {
                            throw new HttpRequestException("HTTP " + (int)response.StatusCode);
                        }

                        json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    }

                    ReleaseInfo release = GitHubReleases.Parse(json);
                    if (!string.Equals(release.Tag, _lastTag, StringComparison.Ordinal))
                    {
                        _lastTag = release.Tag;
                        _ready.Enqueue(release);
                    }
                }
                catch (Exception ex)
                {
                    if ((DateTime.UtcNow - _lastFailureLogged).TotalHours >= 6)
                    {
                        _lastFailureLogged = DateTime.UtcNow;
                        _log.Warn("Update check failed: " + ex.Message);
                    }
                }

                if (_stop.WaitOne(TimeSpan.FromHours(_pollHours)))
                {
                    return;
                }
            }
        }

        private static HttpClient CreateClient()
        {
#if NET48
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
#endif
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CS2-Multiplayer-Server/" + Multiplayer.Core.Protocol.ProtocolConstants.ModVersion);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }
    }
}
