using System;
using System.Globalization;

namespace Multiplayer.Core.Transport
{
    /// <summary>Turns what a player typed into the Join box into a host and a port.</summary>
    public static class EndpointParser
    {
        /// <summary>
        /// Accepts "host", "host:port", "v4addr:port", "[v6addr]:port" and a bare v6 address.
        /// Returns false when the text is empty or the port is not a number in range.
        /// </summary>
        public static bool TryParse(string text, int defaultPort, out string host, out int port)
        {
            host = null;
            port = defaultPort;
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return false;
            }

            if (text.StartsWith("[", StringComparison.Ordinal))
            {
                int close = text.IndexOf(']');
                if (close < 0)
                {
                    return false;
                }

                host = text.Substring(1, close - 1);
                string rest = text.Substring(close + 1);
                if (rest.Length == 0)
                {
                    return host.Length > 0;
                }

                return rest.StartsWith(":", StringComparison.Ordinal) && TryParsePort(rest.Substring(1), out port) && host.Length > 0;
            }

            int firstColon = text.IndexOf(':');
            int lastColon = text.LastIndexOf(':');
            if (firstColon >= 0 && firstColon != lastColon)
            {
                // More than one colon and no brackets: a bare IPv6 address with no port.
                host = text;
                return true;
            }

            if (lastColon < 0)
            {
                host = text;
                return true;
            }

            host = text.Substring(0, lastColon);
            return host.Length > 0 && TryParsePort(text.Substring(lastColon + 1), out port);
        }

        public static bool TryParsePort(string text, out int port)
        {
            port = 0;
            return int.TryParse((text ?? string.Empty).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) && port > 0 && port <= ushort.MaxValue;
        }
    }
}
