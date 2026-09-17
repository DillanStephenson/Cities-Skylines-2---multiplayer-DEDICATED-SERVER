using System.Text;
using Multiplayer.Core.Protocol;

namespace Multiplayer.Core.Session
{
    /// <summary>Input scrubbing shared by server and client.</summary>
    internal static class SessionText
    {
        public static string SanitizeName(string name)
        {
            name = (name ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                name = "Player";
            }

            if (name.Length > ProtocolConstants.MaxPlayerNameLength)
            {
                name = name.Substring(0, ProtocolConstants.MaxPlayerNameLength);
            }

            return name;
        }

        public static string SanitizeChat(string text)
        {
            text = (text ?? string.Empty).Trim();
            if (text.Length > ProtocolConstants.MaxChatLength)
            {
                text = text.Substring(0, ProtocolConstants.MaxChatLength);
            }

            var builder = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                builder.Append(char.IsControl(c) && c != '\n' ? ' ' : c);
            }

            return builder.ToString();
        }
    }
}
