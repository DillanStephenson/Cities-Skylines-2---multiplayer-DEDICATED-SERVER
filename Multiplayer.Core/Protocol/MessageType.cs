namespace Multiplayer.Core.Protocol
{
    /// <summary>First two bytes of every frame. Values are part of the protocol; never renumber, only append.</summary>
    public enum MessageType : ushort
    {
        HandshakeRequest = 1,
        HandshakeResponse = 2,
        Disconnect = 3,
        Heartbeat = 4,
        Chat = 5,
        PlayerList = 6,
        SimulationSpeed = 7,
        GameplayCommand = 8,
        ServerControl = 9,

        /// <summary>Server -> client: what world the server holds, if any.</summary>
        WorldInfo = 10,

        /// <summary>Client -> server: send me the world.</summary>
        WorldRequest = 11,

        /// <summary>A slice of world bytes, in either direction.</summary>
        WorldChunk = 12,

        /// <summary>Owner -> server: a new world is coming.</summary>
        WorldUploadBegin = 13,

        /// <summary>Owner -> server: all chunks sent.</summary>
        WorldUploadEnd = 14,

        /// <summary>Server -> owner: verdict on the upload.</summary>
        WorldUploadResult = 15,
    }
}
