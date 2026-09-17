using System.Collections.Generic;
using Multiplayer.Core.Build;

namespace Multiplayer.Sync
{
    /// <summary>What one other player's tool is currently showing.</summary>
    internal sealed class RemotePreview
    {
        public int PlayerId;
        public PreviewCommand Preview;
        public long SeenAtMs;
    }

    /// <summary>The other players' tool previews. Written by the service as they arrive, drawn by the presence renderer.</summary>
    internal static class PreviewStore
    {
        /// <summary>A preview that stops being refreshed disappears after this long.</summary>
        public const long StaleAfterMs = 1500;

        private static readonly Dictionary<int, RemotePreview> Players = new Dictionary<int, RemotePreview>();

        public static void Receive(int playerId, PreviewCommand command, long nowMs)
        {
            lock (Players)
            {
                if (command == null || command.IsEmpty)
                {
                    Players.Remove(playerId);
                    return;
                }

                Players[playerId] = new RemotePreview { PlayerId = playerId, Preview = command, SeenAtMs = nowMs };
            }
        }

        public static void Remove(int playerId)
        {
            lock (Players)
            {
                Players.Remove(playerId);
            }
        }

        public static void Clear()
        {
            lock (Players)
            {
                Players.Clear();
            }
        }

        public static List<RemotePreview> Snapshot(long nowMs)
        {
            var result = new List<RemotePreview>();
            lock (Players)
            {
                var stale = new List<int>();
                foreach (KeyValuePair<int, RemotePreview> pair in Players)
                {
                    if (nowMs - pair.Value.SeenAtMs > StaleAfterMs)
                    {
                        stale.Add(pair.Key);
                        continue;
                    }

                    result.Add(pair.Value);
                }

                foreach (int id in stale)
                {
                    Players.Remove(id);
                }
            }

            return result;
        }
    }
}
