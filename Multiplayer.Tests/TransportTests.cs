using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Multiplayer.Core.Transport;
using Multiplayer.Core.Transport.Tcp;
using Xunit;

namespace Multiplayer.Tests
{
    public class TransportTests
    {
        [Fact]
        public void Frames_ArriveInOrder_IncludingLargeOnes()
        {
            using var host = TcpHostTransport.Listen(0);
            using var client = TcpClientTransport.Connect("127.0.0.1", host.Port);

            int hostSideId = WaitForConnected(host);
            WaitForConnected(client);

            byte[] tiny = new byte[] { 42 };
            byte[] medium = Filled(100 * 1024, 7);
            byte[] large = Filled(1024 * 1024, 9);
            byte[] empty = new byte[0];

            client.Send(TransportIds.ServerConnectionId, tiny);
            client.Send(TransportIds.ServerConnectionId, medium);
            client.Send(TransportIds.ServerConnectionId, large);
            client.Send(TransportIds.ServerConnectionId, empty);

            List<byte[]> received = CollectData(host, 4);
            Assert.Equal(tiny, received[0]);
            Assert.Equal(medium.Length, received[1].Length);
            Assert.Equal(medium, received[1]);
            Assert.Equal(large.Length, received[2].Length);
            Assert.Equal(large, received[2]);
            Assert.Empty(received[3]);

            // And the other direction.
            host.Send(hostSideId, medium);
            List<byte[]> back = CollectData(client, 1);
            Assert.Equal(medium, back[0]);
        }

        [Fact]
        public void ClientStop_ProducesOneDisconnectOnHost()
        {
            using var host = TcpHostTransport.Listen(0);
            var client = TcpClientTransport.Connect("127.0.0.1", host.Port);

            int id = WaitForConnected(host);
            WaitForConnected(client);
            client.Stop();

            TransportEvent ev = WaitForEvent(host, TransportEventType.Disconnected);
            Assert.Equal(id, ev.ConnectionId);

            Thread.Sleep(50);
            Assert.False(host.TryDequeue(out _));
        }

        [Fact]
        public void GracefulDisconnect_FlushesQueuedFrameFirst()
        {
            using var host = TcpHostTransport.Listen(0);
            using var client = TcpClientTransport.Connect("127.0.0.1", host.Port);

            int id = WaitForConnected(host);
            WaitForConnected(client);

            byte[] farewell = Filled(64 * 1024, 3);
            host.Send(id, farewell);
            host.Disconnect(id, "bye");

            bool gotData = false;
            bool gotDisconnect = false;
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 3000 && !gotDisconnect)
            {
                if (client.TryDequeue(out TransportEvent ev))
                {
                    if (ev.Type == TransportEventType.Data)
                    {
                        Assert.Equal(farewell, ev.Data);
                        gotData = true;
                    }
                    else if (ev.Type == TransportEventType.Disconnected)
                    {
                        gotDisconnect = true;
                    }
                }
                else
                {
                    Thread.Sleep(5);
                }
            }

            Assert.True(gotData, "frame queued before Disconnect should still arrive");
            Assert.True(gotDisconnect);
        }

        [Fact]
        public void ConnectingToClosedPort_ReportsDisconnected()
        {
            using var probe = TcpHostTransport.Listen(0);
            int closedPort = probe.Port;
            probe.Stop();

            using var client = TcpClientTransport.Connect("127.0.0.1", closedPort, 2000);
            TransportEvent ev = WaitForEvent(client, TransportEventType.Disconnected, 5000);
            Assert.False(string.IsNullOrEmpty(ev.Reason));
        }

        private static int WaitForConnected(ITransport transport)
        {
            return WaitForEvent(transport, TransportEventType.Connected).ConnectionId;
        }

        private static TransportEvent WaitForEvent(ITransport transport, TransportEventType type, int timeoutMs = 3000)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                if (transport.TryDequeue(out TransportEvent ev))
                {
                    if (ev.Type == type)
                    {
                        return ev;
                    }
                }
                else
                {
                    Thread.Sleep(5);
                }
            }

            throw new TimeoutException("No " + type + " event within " + timeoutMs + " ms");
        }

        private static List<byte[]> CollectData(ITransport transport, int count, int timeoutMs = 5000)
        {
            var result = new List<byte[]>();
            var clock = Stopwatch.StartNew();
            while (result.Count < count && clock.ElapsedMilliseconds < timeoutMs)
            {
                if (transport.TryDequeue(out TransportEvent ev))
                {
                    if (ev.Type == TransportEventType.Data)
                    {
                        result.Add(ev.Data);
                    }
                }
                else
                {
                    Thread.Sleep(2);
                }
            }

            Assert.Equal(count, result.Count);
            return result;
        }

        private static byte[] Filled(int length, byte seed)
        {
            var data = new byte[length];
            for (int i = 0; i < length; i++)
            {
                data[i] = (byte)(seed + i * 31);
            }

            return data;
        }
    }
}
