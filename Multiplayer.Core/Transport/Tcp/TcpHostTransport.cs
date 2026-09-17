using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Multiplayer.Core.Transport.Tcp
{
    /// <summary>
    /// Listens for clients inside the host's game process. There is no separate server executable:
    /// whoever clicks Host is the server.
    /// </summary>
    public sealed class TcpHostTransport : ITransport
    {
        private readonly ConcurrentQueue<TransportEvent> _events = new ConcurrentQueue<TransportEvent>();
        private readonly ConcurrentDictionary<int, FramedTcpConnection> _connections = new ConcurrentDictionary<int, FramedTcpConnection>();

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private int _nextConnectionId;

        /// <summary>Actual bound port. Differs from the requested one only when 0 was requested (pick any free port).</summary>
        public int Port { get; private set; }

        public bool IsRunning => _running;

        public int ConnectionCount => _connections.Count;

        private TcpHostTransport()
        {
        }

        public static TcpHostTransport Listen(int port)
        {
            if (port < 0 || port > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(port));
            }

            var transport = new TcpHostTransport();
            transport.StartListening(port);
            return transport;
        }

        private void StartListening(int port)
        {
            TcpListener listener;
            try
            {
                // Dual-mode socket accepts both IPv4 and IPv6 clients on one port.
                listener = new TcpListener(IPAddress.IPv6Any, port);
                listener.Server.DualMode = true;
                listener.Start();
            }
            catch (Exception)
            {
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
            }

            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _running = true;

            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "MP-Accept" };
            _acceptThread.Start();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    if (_running)
                    {
                        continue;
                    }

                    break;
                }

                int id = Interlocked.Increment(ref _nextConnectionId);
                FramedTcpConnection connection;
                try
                {
                    connection = new FramedTcpConnection(client, id, _events);
                }
                catch (Exception)
                {
                    try
                    {
                        client.Close();
                    }
                    catch (Exception)
                    {
                    }

                    continue;
                }

                _connections[id] = connection;
                _events.Enqueue(TransportEvent.Connected(id));
                connection.Start();
            }
        }

        public void Send(int connectionId, byte[] payload)
        {
            if (_connections.TryGetValue(connectionId, out FramedTcpConnection connection))
            {
                connection.Send(payload);
            }
        }

        public bool TryDequeue(out TransportEvent transportEvent)
        {
            if (!_events.TryDequeue(out transportEvent))
            {
                return false;
            }

            if (transportEvent.Type == TransportEventType.Disconnected)
            {
                _connections.TryRemove(transportEvent.ConnectionId, out _);
            }

            return true;
        }

        public void Disconnect(int connectionId, string reason)
        {
            if (_connections.TryGetValue(connectionId, out FramedTcpConnection connection))
            {
                connection.Close(reason);
            }
        }

        public void Stop()
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            try
            {
                _listener.Stop();
            }
            catch (Exception)
            {
            }

            foreach (FramedTcpConnection connection in _connections.Values)
            {
                connection.Abort("Server stopped");
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
