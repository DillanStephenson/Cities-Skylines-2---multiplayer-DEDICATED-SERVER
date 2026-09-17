namespace Multiplayer.Core.Session
{
    /// <summary>Where a <see cref="ClientSession"/> is in its life.</summary>
    public enum SessionState
    {
        /// <summary>Not in a session. Join may be called.</summary>
        Offline,

        /// <summary>Socket is being opened.</summary>
        Connecting,

        /// <summary>Socket open, waiting for the server's verdict.</summary>
        Handshaking,

        /// <summary>In a session.</summary>
        Connected,

        /// <summary>Left involuntarily; <see cref="ClientSession.LastError"/> says why. Join may be called again.</summary>
        Failed,
    }
}
