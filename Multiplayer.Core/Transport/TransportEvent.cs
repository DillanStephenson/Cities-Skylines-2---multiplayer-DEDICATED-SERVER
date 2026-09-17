namespace Multiplayer.Core.Transport
{
    public enum TransportEventType
    {
        Connected,
        Disconnected,
        Data,
    }

    /// <summary>
    /// What a transport hands to the session, one per poll. Produced on background threads,
    /// consumed on the game thread, so it is an immutable value.
    /// </summary>
    public readonly struct TransportEvent
    {
        public readonly TransportEventType Type;
        public readonly int ConnectionId;
        public readonly byte[] Data;
        public readonly string Reason;

        private TransportEvent(TransportEventType type, int connectionId, byte[] data, string reason)
        {
            Type = type;
            ConnectionId = connectionId;
            Data = data;
            Reason = reason;
        }

        public static TransportEvent Connected(int connectionId)
        {
            return new TransportEvent(TransportEventType.Connected, connectionId, null, null);
        }

        public static TransportEvent Disconnected(int connectionId, string reason)
        {
            return new TransportEvent(TransportEventType.Disconnected, connectionId, null, reason ?? "Connection closed");
        }

        public static TransportEvent DataReceived(int connectionId, byte[] data)
        {
            return new TransportEvent(TransportEventType.Data, connectionId, data, null);
        }
    }
}
