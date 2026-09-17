using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Multiplayer.Core.Session;
using Multiplayer.Core.Transport;
using Multiplayer.Core.Transport.Tcp;

namespace Multiplayer.Tests
{
    /// <summary>Test harness: one server session plus any number of client sessions, all pumped from this thread over 127.0.0.1.</summary>
    internal sealed class Loopback : IDisposable
    {
        public const string OwnerKey = "owner-secret";

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<ClientSession> _clients = new List<ClientSession>();
        private readonly List<ITransport> _rawTransports = new List<ITransport>();

        public ServerSession Server { get; private set; }

        public TcpHostTransport ServerTransport { get; private set; }

        public long Now => _clock.ElapsedMilliseconds;

        public static ServerConfig FastServerConfig(string password = "")
        {
            return new ServerConfig
            {
                ServerName = "TestServer",
                Password = password,
                OwnerKey = OwnerKey,
                HeartbeatIntervalMs = 40,
                TimeoutMs = 400,
                HandshakeTimeoutMs = 300,
            };
        }

        public static ClientConfig FastClientConfig(string name, string password = "", string ownerKey = "", string[] mods = null)
        {
            return new ClientConfig
            {
                PlayerName = name,
                Password = password,
                OwnerKey = ownerKey,
                Mods = new List<string>(mods ?? new string[0]),
                HeartbeatIntervalMs = 40,
                TimeoutMs = 400,
                HandshakeTimeoutMs = 300,
                ConnectTimeoutMs = 2000,
            };
        }

        public ServerSession StartServer(ServerConfig config = null)
        {
            ServerTransport = TcpHostTransport.Listen(0);
            Server = new ServerSession(config ?? FastServerConfig(), new ConsoleLog("server"));
            Server.Start(ServerTransport, Now);
            return Server;
        }

        public ClientSession StartClient(ClientConfig config)
        {
            var transport = TcpClientTransport.Connect("127.0.0.1", ServerTransport.Port);
            var client = new ClientSession(config, new ConsoleLog(config.PlayerName));
            client.Join(transport, Now);
            _clients.Add(client);
            return client;
        }

        public ClientSession StartClient(string name, string password = "", string ownerKey = "")
        {
            return StartClient(FastClientConfig(name, password, ownerKey));
        }

        /// <summary>The hosting player's game: an ordinary client that presents the owner key.</summary>
        public ClientSession StartOwner(string name = "HostPlayer", string password = "")
        {
            return StartClient(FastClientConfig(name, password, OwnerKey));
        }

        public void Rejoin(ClientSession client)
        {
            client.Join(TcpClientTransport.Connect("127.0.0.1", ServerTransport.Port), Now);
        }

        /// <summary>A bare socket to the server that never speaks the protocol unless the test makes it.</summary>
        public TcpClientTransport RawClient()
        {
            var transport = TcpClientTransport.Connect("127.0.0.1", ServerTransport.Port);
            _rawTransports.Add(transport);
            return transport;
        }

        public void PumpOnce()
        {
            long now = Now;
            Server?.Update(now);
            foreach (ClientSession client in _clients)
            {
                client.Update(now);
            }
        }

        public bool PumpUntil(Func<bool> condition, int timeoutMs = 3000)
        {
            long deadline = Now + timeoutMs;
            while (Now < deadline)
            {
                PumpOnce();
                if (condition())
                {
                    return true;
                }

                Thread.Sleep(5);
            }

            PumpOnce();
            return condition();
        }

        public void PumpFor(int durationMs)
        {
            long deadline = Now + durationMs;
            while (Now < deadline)
            {
                PumpOnce();
                Thread.Sleep(5);
            }
        }

        public void Dispose()
        {
            foreach (ClientSession client in _clients)
            {
                try
                {
                    client.Leave("test over");
                }
                catch (Exception)
                {
                }
            }

            foreach (ITransport transport in _rawTransports)
            {
                transport.Stop();
            }

            Server?.Stop("test over");
            ServerTransport?.Stop();
        }

        private sealed class ConsoleLog : ISessionLog
        {
            private readonly string _tag;

            public ConsoleLog(string tag)
            {
                _tag = tag;
            }

            public void Info(string message) => Console.WriteLine("[" + _tag + "] " + message);

            public void Warn(string message) => Console.WriteLine("[" + _tag + "] WARN " + message);

            public void Error(string message) => Console.WriteLine("[" + _tag + "] ERROR " + message);
        }
    }
}
