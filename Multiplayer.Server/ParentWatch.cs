using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Multiplayer.Server
{
    /// <summary>
    /// Watches the process that spawned the window (the host's game) so the window can exit with it.
    /// Uses a raw process handle on Windows because System.Diagnostics.Process.GetProcessById proved
    /// unreliable from inside the game's launch context; falls back to Process elsewhere.
    /// </summary>
    internal sealed class ParentWatch : IDisposable
    {
        private const uint Synchronize = 0x00100000;
        private const uint WaitObject0 = 0;
        private const uint WaitTimeout = 258;

        private IntPtr _handle;
        private Process _process;

        public int Pid { get; }

        public string Description { get; private set; } = string.Empty;

        public bool IsWatching => _handle != IntPtr.Zero || _process != null;

        private ParentWatch(int pid)
        {
            Pid = pid;
        }

        public static ParentWatch Open(int pid, out string failure)
        {
            failure = null;
            var watch = new ParentWatch(pid);

            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                try
                {
                    watch._handle = OpenProcess(Synchronize, false, pid);
                    if (watch._handle != IntPtr.Zero)
                    {
                        watch.Description = "handle";
                        return watch;
                    }

                    failure = "OpenProcess failed with error " + Marshal.GetLastWin32Error();
                }
                catch (Exception ex)
                {
                    failure = ex.GetType().Name + ": " + ex.Message;
                }
            }

            try
            {
                watch._process = Process.GetProcessById(pid);
                watch.Description = watch._process.ProcessName;
                failure = null;
                return watch;
            }
            catch (Exception ex)
            {
                failure = (failure != null ? failure + "; " : string.Empty) + ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        public bool HasExited()
        {
            if (_handle != IntPtr.Zero)
            {
                uint result = WaitForSingleObject(_handle, 0);
                return result == WaitObject0 || (result != WaitTimeout && result != 0x00000080);
            }

            if (_process != null)
            {
                try
                {
                    return _process.HasExited;
                }
                catch (Exception)
                {
                    return true;
                }
            }

            return false;
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }

            _process?.Dispose();
            _process = null;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
