using System;
using System.Collections.Generic;
using System.Text;

namespace Multiplayer.Server
{
    /// <summary>
    /// The launch banner: an ASCII skyline over "CITIES: SKYLINES II" in a 3x5 block font.
    /// Pure ASCII so it renders in any console, including the legacy one the game spawns.
    /// </summary>
    internal static class Logo
    {
        public const string Title = "CITIES: SKYLINES II";
        public const string Subtitle = "MULTIPLAYER SERVER WINDOW";

        private const int SkylineRows = 6;

        public struct Line
        {
            public string Text;
            public ConsoleColor Color;
        }

        /// <summary>Width of the rendered banner in columns.</summary>
        public static int Width => Title.Length * 4 - 1;

        /// <summary>Rows the banner occupies, blank line included.</summary>
        public static int Height => SkylineRows + 5 + 1 + 1;

        public static List<Line> Render(string version)
        {
            var lines = new List<Line>();
            foreach (string row in Skyline(Width))
            {
                lines.Add(new Line { Text = row, Color = ConsoleColor.DarkCyan });
            }

            foreach (string row in BlockText(Title))
            {
                lines.Add(new Line { Text = row, Color = ConsoleColor.Cyan });
            }

            string sub = Subtitle + "   v" + version;
            int pad = Math.Max(0, (Width - sub.Length) / 2);
            lines.Add(new Line { Text = new string(' ', pad) + sub, Color = ConsoleColor.DarkGray });
            lines.Add(new Line { Text = string.Empty, Color = ConsoleColor.Gray });
            return lines;
        }

        // ------------------------------------------------------------------ skyline

        /// <summary>Solid buildings of varied height with the odd antenna, deterministic so it looks the same every launch.</summary>
        private static string[] Skyline(int width)
        {
            int[] widths = { 4, 3, 5, 2, 4, 6, 3, 5, 4, 3, 6, 4, 5, 3, 4, 5, 4, 6, 3, 5 };
            int[] heights = { 2, 4, 3, 5, 3, 5, 2, 4, 5, 3, 2, 4, 3, 4, 2, 5, 3, 4, 2, 3 };
            bool[] antenna = { false, true, false, true, false, false, false, true, true, false, false, true, false, false, false, true, false, false, true, false };

            var grid = new char[SkylineRows][];
            for (int r = 0; r < SkylineRows; r++)
            {
                grid[r] = new string(' ', width).ToCharArray();
            }

            int col = 1;
            int i = 0;
            while (col < width - 1)
            {
                int w = Math.Min(widths[i % widths.Length], width - 1 - col);
                int h = heights[i % heights.Length];
                for (int c = col; c < col + w; c++)
                {
                    for (int r = SkylineRows - h; r < SkylineRows; r++)
                    {
                        grid[r][c] = '#';
                    }
                }

                if (antenna[i % antenna.Length] && SkylineRows - h - 1 >= 0 && w >= 3)
                {
                    grid[SkylineRows - h - 1][col + w / 2] = '|';
                }

                col += w + 1;
                i++;
            }

            var rows = new string[SkylineRows];
            for (int r = 0; r < SkylineRows; r++)
            {
                rows[r] = new string(grid[r]);
            }

            return rows;
        }

        // ------------------------------------------------------------------ block font

        private static string[] BlockText(string text)
        {
            var rows = new StringBuilder[5];
            for (int r = 0; r < 5; r++)
            {
                rows[r] = new StringBuilder();
            }

            for (int i = 0; i < text.Length; i++)
            {
                string[] glyph = Glyph(text[i]);
                for (int r = 0; r < 5; r++)
                {
                    rows[r].Append(glyph[r]);
                    if (i < text.Length - 1)
                    {
                        rows[r].Append(' ');
                    }
                }
            }

            var result = new string[5];
            for (int r = 0; r < 5; r++)
            {
                result[r] = rows[r].ToString().TrimEnd();
            }

            return result;
        }

        /// <summary>3 columns by 5 rows, '#' on, ' ' off.</summary>
        private static string[] Glyph(char c)
        {
            switch (char.ToUpperInvariant(c))
            {
                case 'A': return new[] { " # ", "# #", "###", "# #", "# #" };
                case 'B': return new[] { "## ", "# #", "## ", "# #", "## " };
                case 'C': return new[] { " ##", "#  ", "#  ", "#  ", " ##" };
                case 'D': return new[] { "## ", "# #", "# #", "# #", "## " };
                case 'E': return new[] { "###", "#  ", "## ", "#  ", "###" };
                case 'F': return new[] { "###", "#  ", "## ", "#  ", "#  " };
                case 'G': return new[] { " ##", "#  ", "# #", "# #", " ##" };
                case 'H': return new[] { "# #", "# #", "###", "# #", "# #" };
                case 'I': return new[] { "###", " # ", " # ", " # ", "###" };
                case 'J': return new[] { "  #", "  #", "  #", "# #", " # " };
                case 'K': return new[] { "# #", "# #", "## ", "# #", "# #" };
                case 'L': return new[] { "#  ", "#  ", "#  ", "#  ", "###" };
                case 'M': return new[] { "# #", "###", "###", "# #", "# #" };
                case 'N': return new[] { "## ", "# #", "# #", "# #", "# #" };
                case 'O': return new[] { " # ", "# #", "# #", "# #", " # " };
                case 'P': return new[] { "## ", "# #", "## ", "#  ", "#  " };
                case 'Q': return new[] { " # ", "# #", "# #", " # ", "  #" };
                case 'R': return new[] { "## ", "# #", "## ", "# #", "# #" };
                case 'S': return new[] { " ##", "#  ", " # ", "  #", "## " };
                case 'T': return new[] { "###", " # ", " # ", " # ", " # " };
                case 'U': return new[] { "# #", "# #", "# #", "# #", "###" };
                case 'V': return new[] { "# #", "# #", "# #", "# #", " # " };
                case 'W': return new[] { "# #", "# #", "###", "###", "# #" };
                case 'X': return new[] { "# #", "# #", " # ", "# #", "# #" };
                case 'Y': return new[] { "# #", "# #", " # ", " # ", " # " };
                case 'Z': return new[] { "###", "  #", " # ", "#  ", "###" };
                case '0': return new[] { " # ", "# #", "# #", "# #", " # " };
                case '1': return new[] { " # ", "## ", " # ", " # ", "###" };
                case '2': return new[] { "## ", "  #", " # ", "#  ", "###" };
                case '3': return new[] { "###", "  #", " ##", "  #", "###" };
                case '4': return new[] { "# #", "# #", "###", "  #", "  #" };
                case '5': return new[] { "###", "#  ", "## ", "  #", "## " };
                case '6': return new[] { " ##", "#  ", "## ", "# #", " # " };
                case '7': return new[] { "###", "  #", " # ", " # ", " # " };
                case '8': return new[] { " # ", "# #", " # ", "# #", " # " };
                case '9': return new[] { " # ", "# #", " ##", "  #", "## " };
                case ':': return new[] { "   ", " # ", "   ", " # ", "   " };
                case '.': return new[] { "   ", "   ", "   ", "   ", " # " };
                case '-': return new[] { "   ", "   ", "###", "   ", "   " };
                default: return new[] { "   ", "   ", "   ", "   ", "   " };
            }
        }
    }
}
