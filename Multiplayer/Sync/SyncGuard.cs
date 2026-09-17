namespace Multiplayer.Sync
{
    /// <summary>
    /// Flags shared between the capture and replay systems. Set for the one frame in which a remote
    /// command is applied so the capture system does not echo it back to the server.
    /// </summary>
    internal static class SyncGuard
    {
        /// <summary>True while the apply phase of this frame realises definitions that came from another player.</summary>
        public static bool IsReplaying;

        /// <summary>Dev test: apply a locally made command but still let the capture system send it out.</summary>
        public static bool CaptureAnyway;
    }
}
