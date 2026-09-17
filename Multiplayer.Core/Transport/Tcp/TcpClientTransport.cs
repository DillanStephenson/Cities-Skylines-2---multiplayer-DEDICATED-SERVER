using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Multiplayer.Core.Transport.Tcp
{
    /// <summary>Connects to a host in the background and then behaves like a single-connection transport.</summary>
    public sealed class TcpClientTransport : ITransport
    {
        private readonly ConcurrentQueue<TransportEvent> _events = new ConcurrentQueue<TransportEvent>();
        private FramedTcpConnection _connection;
        private volatile bool _running;

        public bool IsRunning => _running;

        private TcpClientTransport()
        {
        }

        public static TcpClientTransport Connect(string host, int port, int timeoutMs = 8000)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("Host address is empty", nameof(host));
            }

            if (port <= 0 || port > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(port));
            }

            var transport = new TcpClientTransport { _running = true };
            var thread = new Thread(() => transport.ConnectWorker(host.Trim(), port, timeoutMs)) { IsBackground = true, Name = "MP-Connect" };
            thread.Start();
            return transport;
        }

        private void ConnectWorker(string host, int port, int timeoutMs)
        {
            TcpClient client = null;
            try
            {
                IPAddress address = ResolveAddress(host);
                client = new TcpClient(address.AddressFamily);
                IAsyncResult asyncResult = client.BeginConnect(address, port, null, null);
                if (!asyncResult.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    client.Close();
                    _events.Enqueue(TransportEvent.Disconnected(TransportIds.ServerConnectionId, "Connection timed out"));
                    return;
                }

                client.EndConnect(asyncResult);
                if (!_running)
                {
                    client.Close();
                    return;
                }

                var connection = new FramedTcpConnection(client, TransportIds.ServerConnectionId, _events);
                _connection = connection;
                _events.Enqueue(TransportEvent.Connected(TransportIds.ServerConnectionId));
                connection.Start();

                if (!_running)
                {
                    connection.Abort("Client stopped");
                }
            }
            catch (Exception ex)
            {
                try
                {
                    client?.Close();
                }
                catch (Exception)
                {
                }

                _events.Enqueue(TransportEvent.Disconnected(TransportIds.ServerConnectionId, ex.Message));
            }
        }

        private static IPAddress ResolveAddress(string host)
        {
            if (IPAddress.TryParse(host, out IPAddress literal))
            {
                return literal;
            }

            IPAddress[] addresses = Dns.GetHostAddresses(host);
            IPAddress fallback = null;
            foreach (IPAddress address in addresses)
            {
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    return address;
                }

                if (fallback == null && address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    fallback = address;
                }
            }

            if (fallback == null)
            {
                throw new SocketException((int)SocketError.HostNotFound);
            }

            return fallback;
        }

        public void Send(int connectionId, byte[] payload)
        {
            _connection?.Send(payload);
        }

        public bool TryDequeue(out TransportEvent transportEvent)
        {
            return _events.TryDequeue(out transportEvent);
        }

        public void Disconnect(int connectionId, string reason)
        {
            _connection?.Close(reason);
        }

        public void Stop()
        {
            _running = false;
            _connection?.Abort("Client stopped");
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
