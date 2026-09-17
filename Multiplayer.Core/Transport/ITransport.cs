using System;

namespace Multiplayer.Core.Transport
{
    /// <summary>
    /// A framed, reliable, ordered byte pipe to one or more peers. The session never touches sockets;
    /// it only sends frames and drains events, which is what lets a Steam relay transport slot in later.
    /// For a host transport, connection ids are assigned per accepted client. For a client transport,
    /// the single connection to the host always has id <see cref="ServerConnectionId"/>.
    /// </summary>
    public interface ITransport : IDisposable
    {
        bool IsRunning { get; }

        /// <summary>Queue one frame for delivery. Never blocks the caller; failures surface as a Disconnected event.</summary>
        void Send(int connectionId, byte[] payload);

        /// <summary>Pull the next event, if any. Call from one thread only (the game thread).</summary>
        bool TryDequeue(out TransportEvent transportEvent);

        /// <summary>Close one connection after flushing whatever was already queued to it.</summary>
        void Disconnect(int connectionId, string reason);

        /// <summary>Tear everything down immediately.</summary>
        void Stop();
    }

    public static class TransportIds
    {
        /// <summary>Connection id a client transport uses for its one and only peer, the host.</summary>
        public const int ServerConnectionId = 0;
    }
}
