using System;
using System.Collections.Generic;
using System.Text;
using Multiplayer.Core.Protocol;
using Multiplayer.Core.Session;

namespace Multiplayer.Server
{
    /// <summary>
    /// The window. Panel mode: the logo and a pinned status block on top, a scrolling colour-coded event log,
    /// a command line at the bottom; every row is positioned explicitly so nothing drifts on resize or scroll.
    /// Plain mode (redirected output, systemd, --plain): one line per event.
    /// </summary>
    internal sealed class ConsoleView : ISessionLog
    {
        private const int LogCapacity = 500;
        private const int StatusRows = 5;

        private struct Entry
        {
            public string Text;
            public ConsoleColor Color;
        }

        private readonly object _gate = new object();
        private readonly List<Entry> _entries = new List<Entry>();
        private readonly StringBuilder _input = new StringBuilder();
        private readonly List<Logo.Line> _logo;
        private bool _panel;
        private int _lastWidth = -1;
        private int _lastHeight = -1;

        public bool PanelMode => _panel;

        // Windows consoles start in QuickEdit mode: one click selects text and every Console.Write blocks until
        // the selection is dismissed, which stalls the whole server. Turned off; players can still Ctrl+C to quit.
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int handle);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr handle, uint mode);

        private static void DisableQuickEdit()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                return;
            }

            try
            {
                const int StdInput = -10;
                const uint QuickEdit = 0x0040;
                const uint ExtendedFlags = 0x0080;
                IntPtr input = GetStdHandle(StdInput);
                if (GetConsoleMode(input, out uint mode))
                {
                    SetConsoleMode(input, (mode & ~QuickEdit) | ExtendedFlags);
                }
            }
            catch (Exception)
            {
                // Not a real console; nothing to do.
            }
        }

        public ConsoleView(bool plain)
        {
            _logo = Logo.Render(ProtocolConstants.ModVersion);
            _panel = !plain;
            bool redirected;
            try
            {
                redirected = Console.IsOutputRedirected || Console.IsInputRedirected;
            }
            catch (Exception)
            {
                redirected = true;
            }

            if (redirected)
            {
                _panel = false;
            }

            if (_panel)
            {
                DisableQuickEdit();
                try
                {
                    Console.CursorVisible = true;
                    Console.Clear();
                }
                catch (Exception)
                {
                    _panel = false;
                }
            }
            else if (!redirected)
            {
                // --plain in a real terminal: still say hello once.
                foreach (Logo.Line line in _logo)
                {
                    WriteColored(line.Text, line.Color);
                    Console.WriteLine();
                }
            }
        }

        // ------------------------------------------------------------------ log input

        public void Info(string message) => Append(message, ConsoleColor.Gray);

        public void Warn(string message) => Append(message, ConsoleColor.Yellow);

        public void Error(string message) => Append(message, ConsoleColor.Red);

        public void Append(string message, ConsoleColor color = ConsoleColor.Gray)
        {
            string text = DateTime.Now.ToString("HH:mm:ss") + "  " + (message ?? string.Empty).Replace("\r", "").Replace("\n", " | ");
            FileLog.Write(message);
            lock (_gate)
            {
                _entries.Add(new Entry { Text = text, Color = color });
                if (_entries.Count > LogCapacity)
                {
                    _entries.RemoveRange(0, _entries.Count - LogCapacity);
                }
            }

            if (!_panel)
            {
                WriteColored(text, color);
                Console.WriteLine();
            }
        }

        // ------------------------------------------------------------------ command input

        /// <summary>Panel mode only: consume typed keys; returns a full command when Enter was pressed.</summary>
        public string PollCommand()
        {
            if (!_panel)
            {
                return null;
            }

            try
            {
                while (Console.KeyAvailable)
                {
                    ConsoleKeyInfo key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Enter)
                    {
                        string command = _input.ToString();
                        _input.Clear();
                        return command;
                    }

                    if (key.Key == ConsoleKey.Backspace)
                    {
                        if (_input.Length > 0)
                        {
                            _input.Length--;
                        }
                    }
                    else if (key.Key == ConsoleKey.Escape)
                    {
                        _input.Clear();
                    }
                    else if (!char.IsControl(key.KeyChar))
                    {
                        _input.Append(key.KeyChar);
                    }
                }
            }
            catch (Exception)
            {
                _panel = false;
            }

            return null;
        }

        // ------------------------------------------------------------------ drawing

        public void Draw(ServerSession session, ServerOptions options, long nowMs, string extraStatus)
        {
            if (!_panel)
            {
                return;
            }

            try
            {
                int width = Math.Max(40, Console.WindowWidth);
                int height = Math.Max(StatusRows + 6, Console.WindowHeight);
                if (width != _lastWidth || height != _lastHeight)
                {
                    Console.Clear();
                    _lastWidth = width;
                    _lastHeight = height;
                }

                int top = Console.WindowTop;
                var rows = new List<Entry>(height);

                bool showLogo = width >= Logo.Width + 2 && height >= Logo.Height + StatusRows + 8;
                if (showLogo)
                {
                    foreach (Logo.Line line in _logo)
                    {
                        rows.Add(new Entry { Text = " " + line.Text, Color = line.Color });
                    }
                }

                TimeSpan uptime = TimeSpan.FromMilliseconds(Math.Max(0, nowMs - session.StartedAtMs));
                string listening = session.IsRunning ? "port " + options.Port : "STOPPED";
                rows.Add(new Entry
                {
                    Text = " " + options.ServerName + "   " + listening + "   password " + (string.IsNullOrEmpty(options.Password) ? "none" : "set") + "   up " + uptime.ToString(@"hh\:mm\:ss"),
                    Color = ConsoleColor.White,
                });
                rows.Add(new Entry
                {
                    Text = " owner key " + options.OwnerKey + (options.OwnerKeyGenerated ? "   (type it under Join > Owner key in the game to be the host)" : "   (issued to the host's game)"),
                    Color = options.OwnerKeyGenerated ? ConsoleColor.Yellow : ConsoleColor.DarkGray,
                });
                rows.Add(new Entry
                {
                    Text = " players " + session.Players.Count + "/" + options.MaxPlayers + ": " + (session.Players.Count == 0 ? "none yet" : Join(session.Players)) + (session.PendingSockets > 0 ? "   (+" + session.PendingSockets + " connecting)" : ""),
                    Color = session.Players.Count > 0 ? ConsoleColor.Green : ConsoleColor.DarkGray,
                });
                rows.Add(new Entry
                {
                    Text = " speed " + FormatSpeed(session.SimulationSpeed) + "   in " + FormatBytes(session.BytesIn) + "   out " + FormatBytes(session.BytesOut) + (string.IsNullOrEmpty(extraStatus) ? "" : "   " + extraStatus),
                    Color = ConsoleColor.Gray,
                });
                string worldText;
                ConsoleColor worldColor;
                if (session.IsReceivingWorld)
                {
                    worldText = " world: receiving from the host " + (session.UploadTotalBytes > 0 ? (100 * session.UploadReceivedBytes / session.UploadTotalBytes) + "%" : "") + " (" + FormatBytes(session.UploadReceivedBytes) + " of " + FormatBytes(session.UploadTotalBytes) + ")";
                    worldColor = ConsoleColor.Cyan;
                }
                else if (session.World != null)
                {
                    worldText = " world: " + session.World.Info + "   by " + session.World.Info.UploaderName + " at " + DateTimeOffset.FromUnixTimeSeconds(session.World.Info.SavedAtUnix).ToLocalTime().ToString("HH:mm")
                        + (session.ReferenceMods != null ? "   mods required: " + session.ReferenceMods.Count : "");
                    worldColor = ConsoleColor.White;
                }
                else
                {
                    worldText = " world: none yet (uploaded once the host is in a city)" + (session.ReferenceMods != null ? "   mods required: " + session.ReferenceMods.Count : "");
                    worldColor = ConsoleColor.DarkGray;
                }

                rows.Add(new Entry { Text = worldText, Color = worldColor });
                rows.Add(new Entry { Text = new string('-', width - 1), Color = ConsoleColor.DarkGray });

                int logRows = height - rows.Count - 2;
                List<Entry> tail;
                lock (_gate)
                {
                    int start = Math.Max(0, _entries.Count - logRows);
                    tail = _entries.GetRange(start, _entries.Count - start);
                }

                for (int i = 0; i < logRows; i++)
                {
                    rows.Add(i < tail.Count ? tail[i] : new Entry { Text = string.Empty, Color = ConsoleColor.Gray });
                }

                rows.Add(new Entry { Text = new string('-', width - 1), Color = ConsoleColor.DarkGray });
                string prompt = "> " + _input;
                rows.Add(new Entry { Text = prompt, Color = ConsoleColor.White });

                for (int r = 0; r < rows.Count && r < height; r++)
                {
                    Console.SetCursorPosition(0, top + r);
                    WriteColored(Fit(rows[r].Text, width), rows[r].Color);
                }

                Console.ResetColor();
                Console.SetCursorPosition(Math.Min(prompt.Length, width - 2), top + Math.Min(rows.Count, height) - 1);
            }
            catch (Exception)
            {
                // Console went away or was resized mid-draw; try again next tick.
            }
        }

        public void Shutdown()
        {
            try
            {
                Console.ResetColor();
                if (_panel)
                {
                    Console.SetCursorPosition(0, Console.WindowTop + Math.Max(0, Console.WindowHeight - 1));
                    Console.WriteLine();
                }
            }
            catch (Exception)
            {
            }
        }

        // ------------------------------------------------------------------ helpers

        private static void WriteColored(string text, ConsoleColor color)
        {
            try
            {
                Console.ForegroundColor = color;
                Console.Write(text);
                Console.ResetColor();
            }
            catch (Exception)
            {
                Console.Write(text);
            }
        }

        /// <summary>Exactly width-1 characters, so a row never spills onto the next one.</summary>
        private static string Fit(string text, int width)
        {
            text = text ?? string.Empty;
            int cells = width - 1;
            return text.Length >= cells ? text.Substring(0, cells) : text.PadRight(cells);
        }

        private static string Join(IReadOnlyList<PlayerInfo> players)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < players.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(players[i].PlayerId).Append(':').Append(players[i]);
            }

            return builder.ToString();
        }

        public static string FormatSpeed(float speed)
        {
            return speed <= 0f ? "paused" : speed.ToString("0.#") + "x";
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes + " B";
            }

            if (bytes < 1024 * 1024)
            {
                return (bytes / 1024.0).ToString("0.0") + " KB";
            }

            return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
        }
    }
}
