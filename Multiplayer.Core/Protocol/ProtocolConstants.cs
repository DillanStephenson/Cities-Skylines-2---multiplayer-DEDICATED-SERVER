namespace Multiplayer.Core.Protocol
{
    /// <summary>
    /// Numbers every peer must agree on. Bump <see cref="ProtocolVersion"/> whenever a message
    /// layout changes so mismatched builds refuse each other at handshake instead of desyncing later.
    /// </summary>
    public static class ProtocolConstants
    {
        /// <summary>2: mod list in the handshake, world transfer messages.</summary>
        public const int ProtocolVersion = 3;

        /// <summary>Mod version advertised in the handshake. Keep in step with PublishConfiguration.xml.</summary>
        public const string ModVersion = "0.2.1";

        public const int DefaultPort = 27015;

        /// <summary>Largest single frame the TCP transport will accept. Bigger payloads must be chunked by the session.</summary>
        public const int MaxFrameBytes = 4 * 1024 * 1024;

        /// <summary>World bytes per WorldChunk frame.</summary>
        public const int WorldChunkBytes = 256 * 1024;

        /// <summary>Refuse worlds bigger than this (a save is typically 30 to 200 MB).</summary>
        public const long MaxWorldBytes = 1024L * 1024L * 1024L;

        public const int MaxPlayerNameLength = 24;
        public const int MaxChatLength = 500;
        public const int MaxPlayers = 16;
        public const int MaxModListEntries = 2000;

        /// <summary>Chat and commands authored by the server console carry this player id.</summary>
        public const int ServerPlayerId = 0;

        /// <summary>Players are numbered from here upward, in join order.</summary>
        public const int FirstPlayerId = 1;
    }
}
