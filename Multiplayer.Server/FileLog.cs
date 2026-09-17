using System;
using System.IO;
using System.Reflection;

namespace Multiplayer.Server
{
    /// <summary>
    /// Everything the window shows also goes to Multiplayer.Server.log next to the executable, truncated at
    /// each start, so a window that closed can still be diagnosed. Failures to write are ignored.
    /// </summary>
    internal static class FileLog
    {
        private static readonly object Gate = new object();
        private static StreamWriter _writer;

        public static string Path { get; private set; } = "Multiplayer.Server.log";

        public static void Open()
        {
            try
            {
                string directory = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
                Path = System.IO.Path.Combine(directory, "Multiplayer.Server.log");
                _writer = new StreamWriter(Path, false) { AutoFlush = true };
            }
            catch (Exception)
            {
                _writer = null;
            }
        }

        public static void Write(string line)
        {
            lock (Gate)
            {
                try
                {
                    _writer?.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
