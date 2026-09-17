using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using Multiplayer.Core.Protocol;

namespace Multiplayer.Core.Transport.Tcp
{
    /// <summary>
    /// One TCP socket carrying length-prefixed frames: [int32 little-endian length][payload].
    /// A receive thread turns incoming frames into Data events; a send thread drains an outbound queue
    /// so the game thread never blocks on a slow peer. Exactly one Disconnected event is raised per connection.
    /// </summary>
    internal sealed class FramedTcpConnection
    {
        private const int HeaderBytes = 4;

        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly ConcurrentQueue<TransportEvent> _events;
        private readonly BlockingCollection<byte[]> _sendQueue = new BlockingCollection<byte[]>();

        private const int GracefulCloseLimitMs = 3000;

        private int _closed;
        private int _disconnectRaised;
        private volatile bool _gracefulClose;
        private volatile string _closeReason;
        private Timer _forceCloseTimer;

        /// <summary>Set when the receive loop has seen the peer close (or fail); the graceful close waits on it.</summary>
        private readonly ManualResetEventSlim _receiveEnded = new ManualResetEventSlim(false);

        /// <summary>How long a graceful close waits for the peer to read the farewell and close its side.</summary>
        private const int GracefulDrainMs = 1500;

        public int Id { get; }

        public bool IsOpen => Volatile.Read(ref _closed) == 0;

        public FramedTcpConnection(TcpClient client, int id, ConcurrentQueue<TransportEvent> events)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _events = events ?? throw new ArgumentNullException(nameof(events));
            Id = id;
            _client.NoDelay = true;
            _stream = _client.GetStream();
        }

        public void Start()
        {
            var receive = new Thread(ReceiveLoop) { IsBackground = true, Name = "MP-Recv-" + Id };
            var send = new Thread(SendLoop) { IsBackground = true, Name = "MP-Send-" + Id };
            receive.Start();
            send.Start();
        }

        public void Send(byte[] payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            if (payload.Length > ProtocolConstants.MaxFrameBytes)
            {
                throw new ArgumentException($"Frame of {payload.Length} bytes exceeds MaxFrameBytes");
            }

            if (!IsOpen)
            {
                return;
            }

            try
            {
                _sendQueue.Add(payload);
            }
            catch (InvalidOperationException)
            {
                // Queue completed by a concurrent Close; the frame is dropped, which is fine because the peer is going away.
            }
        }

        /// <summary>
        /// Flush queued frames, then close. The Disconnected event follows once the socket is really shut.
        /// A later Abort does not cut the flush short; a timer force-closes if the peer stops reading.
        /// </summary>
        public void Close(string reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            _closeReason = reason ?? "Closed";
            _gracefulClose = true;
            try
            {
                _sendQueue.CompleteAdding();
            }
            catch (ObjectDisposedException)
            {
            }

            _forceCloseTimer = new Timer(_ => CloseSocket(), null, GracefulCloseLimitMs, Timeout.Infinite);
        }

        /// <summary>Close right now, dropping anything still queued (unless a graceful close is already flushing).</summary>
        public void Abort(string reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                _closeReason = reason ?? "Aborted";
                try
                {
                    _sendQueue.CompleteAdding();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            if (_gracefulClose)
            {
                // Let the send loop finish delivering the farewell; the timer guards against a stuck peer.
                return;
            }

            CloseSocket();
        }

        private void SendLoop()
        {
            try
            {
                var header = new byte[HeaderBytes];
                foreach (byte[] payload in _sendQueue.GetConsumingEnumerable())
                {
                    WriteInt32LittleEndian(header, payload.Length);
                    _stream.Write(header, 0, HeaderBytes);
                    if (payload.Length > 0)
                    {
                        _stream.Write(payload, 0, payload.Length);
                    }
                }

                // Reached only when Close() completed the queue: everything queued has been written.
                try
                {
                    _client.Client.Shutdown(SocketShutdown.Send);
                }
                catch (Exception)
                {
                }

                // Let the peer read the farewell and close its side first. Closing our socket while it still
                // holds unread data makes Linux answer with a reset, which throws away what we just sent
                // (the rejection reason, the kick message). The receive loop keeps draining meanwhile.
                _receiveEnded.Wait(GracefulDrainMs);
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref _closed, 1) == 0)
                {
                    _closeReason = "Send failed: " + ex.Message;
                }
            }
            finally
            {
                // Queue drained (or send failed): the farewell, if any, is out; shut the socket for real.
                CloseSocket();
            }
        }

        private void ReceiveLoop()
        {
            string reason = "Connection closed by remote";
            try
            {
                var header = new byte[HeaderBytes];
                while (true)
                {
                    if (!ReadExact(header, HeaderBytes))
                    {
                        break;
                    }

                    int length = ReadInt32LittleEndian(header);
                    if (length < 0 || length > ProtocolConstants.MaxFrameBytes)
                    {
                        reason = $"Invalid frame length {length}";
                        break;
                    }

                    var payload = new byte[length];
                    if (length > 0 && !ReadExact(payload, length))
                    {
                        break;
                    }

                    _events.Enqueue(TransportEvent.DataReceived(Id, payload));
                }
            }
            catch (Exception ex)
            {
                if (IsOpen)
                {
                    reason = "Receive failed: " + ex.Message;
                }
            }
            finally
            {
                _receiveEnded.Set();
                string closeReason = _closeReason;
                Abort(closeReason ?? reason);
                RaiseDisconnected(closeReason ?? reason);
            }
        }

        private bool ReadExact(byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = _stream.Read(buffer, offset, count - offset);
                if (read <= 0)
                {
                    return false;
                }

                offset += read;
            }

            return true;
        }

        private void RaiseDisconnected(string reason)
        {
            if (Interlocked.Exchange(ref _disconnectRaised, 1) != 0)
            {
                return;
            }

            _events.Enqueue(TransportEvent.Disconnected(Id, reason));
        }

        private void CloseSocket()
        {
            try
            {
                _stream.Close();
            }
            catch (Exception)
            {
            }

            try
            {
                _client.Close();
            }
            catch (Exception)
            {
            }

            Timer timer = _forceCloseTimer;
            _forceCloseTimer = null;
            try
            {
                timer?.Dispose();
            }
            catch (Exception)
            {
            }
        }

        private static void WriteInt32LittleEndian(byte[] buffer, int value)
        {
            buffer[0] = (byte)value;
            buffer[1] = (byte)(value >> 8);
            buffer[2] = (byte)(value >> 16);
            buffer[3] = (byte)(value >> 24);
        }

        private static int ReadInt32LittleEndian(byte[] buffer)
        {
            return buffer[0] | (buffer[1] << 8) | (buffer[2] << 16) | (buffer[3] << 24);
        }
    }
}
