namespace Multiplayer.Core.Session
{
    public sealed class PlayerInfo
    {
        public int PlayerId { get; }

        public string Name { get; }

        /// <summary>The hosting player: the one who presented the owner key. Has authority for decisions like which world is loaded.</summary>
        public bool IsOwner { get; }

        public PlayerInfo(int playerId, string name, bool isOwner)
        {
            PlayerId = playerId;
            Name = name ?? string.Empty;
            IsOwner = isOwner;
        }

        public override string ToString()
        {
            return IsOwner ? Name + " (host)" : Name;
        }
    }
}
