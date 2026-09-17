using System;

namespace Multiplayer.Core.Protocol
{
    /// <summary>Raised when bytes from the wire cannot be turned into a message. The peer that sent them gets dropped.</summary>
    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message)
        {
        }

        public ProtocolException(string message, Exception inner) : base(message, inner)
        {
        }
    }
}
