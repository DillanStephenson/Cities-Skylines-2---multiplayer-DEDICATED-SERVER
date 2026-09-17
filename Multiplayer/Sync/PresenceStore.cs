using System.Collections.Generic;
using Multiplayer.Core.Build;
using Unity.Mathematics;
using UnityEngine;

namespace Multiplayer.Sync
{
    /// <summary>The last known place of one other player.</summary>
    internal sealed class RemotePresence
    {
        public int PlayerId;
        public string Name = string.Empty;
        public float3 Pivot;
        public float Yaw;
        public float Zoom;
        public bool HasCursor;
        public float3 Cursor;
        public long SeenAtMs;
    }

    /// <summary>Where the other players are. Written by the service as presence commands arrive, read by the drawing and UI systems.</summary>
    internal static class PresenceStore
    {
        /// <summary>After this long without news a marker fades out.</summary>
        public const long StaleAfterMs = 20000;

        private static readonly Dictionary<int, RemotePresence> Players = new Dictionary<int, RemotePresence>();

        private static readonly Color[] Palette =
        {
            new Color(1.00f, 0.62f, 0.20f),
            new Color(0.35f, 0.85f, 1.00f),
            new Color(0.55f, 0.95f, 0.45f),
            new Color(1.00f, 0.45f, 0.70f),
            new Color(0.95f, 0.90f, 0.35f),
            new Color(0.70f, 0.55f, 1.00f),
            new Color(0.40f, 1.00f, 0.85f),
            new Color(1.00f, 0.35f, 0.35f),
        };

        public static void Receive(int playerId, string name, PresenceCommand command, long nowMs)
        {
            if (!Players.TryGetValue(playerId, out RemotePresence presence))
            {
                presence = new RemotePresence { PlayerId = playerId };
                Players[playerId] = presence;
            }

            presence.Name = string.IsNullOrEmpty(name) ? "Player " + playerId : name;
            presence.Pivot = EntityResolver.ToFloat3(command.Pivot);
            presence.Yaw = command.Yaw;
            presence.Zoom = command.Zoom;
            presence.HasCursor = command.HasCursor;
            presence.Cursor = EntityResolver.ToFloat3(command.Cursor);
            presence.SeenAtMs = nowMs;
        }

        public static void Remove(int playerId)
        {
            Players.Remove(playerId);
        }

        public static void Clear()
        {
            Players.Clear();
        }

        public static int Count => Players.Count;

        /// <summary>Current markers, oldest news first; stale ones are dropped on the way out.</summary>
        public static List<RemotePresence> Snapshot(long nowMs)
        {
            var result = new List<RemotePresence>(Players.Count);
            var stale = new List<int>();
            foreach (RemotePresence presence in Players.Values)
            {
                if (nowMs - presence.SeenAtMs > StaleAfterMs)
                {
                    stale.Add(presence.PlayerId);
                }
                else
                {
                    result.Add(presence);
                }
            }

            foreach (int id in stale)
            {
                Players.Remove(id);
            }

            result.Sort((a, b) => a.PlayerId.CompareTo(b.PlayerId));
            return result;
        }

        public static Color ColorFor(int playerId)
        {
            return Palette[((playerId % Palette.Length) + Palette.Length) % Palette.Length];
        }

        public static string HexFor(int playerId)
        {
            Color c = ColorFor(playerId);
            return "#" + ((int)(c.r * 255)).ToString("x2") + ((int)(c.g * 255)).ToString("x2") + ((int)(c.b * 255)).ToString("x2");
        }
    }
}
