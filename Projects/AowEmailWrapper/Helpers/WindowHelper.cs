using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AowEmailWrapper.Helpers
{
    /// <summary>Brings another program's window to the front, and lets another program bring ours.</summary>
    public static class WindowHelper
    {
        private const int SW_RESTORE = 9;
        private const int ASFW_ANY = -1;

        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int dwProcessId);

        /// <summary>
        /// Restores the process's main window if it is minimized and puts it in front. Returns false when
        /// the process has exited or has no window yet (a game still loading).
        /// </summary>
        public static bool BringToFront(Process process)
        {
            if (process == null)
            {
                return false;
            }
            try
            {
                process.Refresh();
                if (process.HasExited)
                {
                    return false;
                }
                IntPtr window = process.MainWindowHandle;
                if (window == IntPtr.Zero)
                {
                    return false;
                }
                if (IsIconic(window))
                {
                    ShowWindow(window, SW_RESTORE);
                }
                SetForegroundWindow(window);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>Lets another process take the foreground; Windows allows it only to the one the player is using.</summary>
        public static void AllowOthersToComeToFront()
        {
            AllowSetForegroundWindow(ASFW_ANY);
        }
    }
}
