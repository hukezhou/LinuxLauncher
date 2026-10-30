using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace vllmLauncher
{
    public partial class App
    {
        #region Win32 P/Invoke for window activation

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc ewProc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const int SW_RESTORE = 9;

        #endregion

        public App()
        {
            DispatcherUnhandledException += (s, e) =>
            {
                e.Handled = true;
                MessageBox.Show($"Error: {e.Exception.Message}\n\n{e.Exception.StackTrace}", "Unhandled Exception", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                {
                    MessageBox.Show($"Fatal Error: {ex.Message}\n\n{ex.StackTrace}", "Fatal Exception", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Check for existing instance BEFORE MainWindow is created (and config.json is loaded)
            if (TryFindAndActivateExistingInstance())
            {
                // Found existing instance, activated its window, shutdown this new instance
                Current.Shutdown();
                return;
            }

            // No existing instance found, proceed with normal startup
            base.OnStartup(e);
        }

        /// <summary>
        /// Check if another instance of this application is already running.
        /// If found, bring its window to foreground and return true.
        /// If not found, return false.
        /// </summary>
        private bool TryFindAndActivateExistingInstance()
        {
            string currentProcessName = Process.GetCurrentProcess().ProcessName;
            int currentPid = Process.GetCurrentProcess().Id;

            Process[] sameNameProcesses = Process.GetProcessesByName(currentProcessName);

            foreach (Process proc in sameNameProcesses)
            {
                // Skip ourselves
                if (proc.Id == currentPid) continue;

                // Found an existing instance, try to activate its window
                IntPtr hwnd = FindWindowByProcess(proc);
                if (hwnd != IntPtr.Zero)
                {
                    ForceForegroundWindow(hwnd);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Find a visible window belonging to the given process.
        /// </summary>
        private IntPtr FindWindowByProcess(Process process)
        {
            // Try MainWindowHandle first
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                return process.MainWindowHandle;
            }

            // Fallback: enumerate all windows and find one belonging to this process
            IntPtr found = IntPtr.Zero;
            uint targetPid = (uint)process.Id;

            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pid == targetPid)
                {
                    // Check if window has a title (visible window)
                    int len = GetWindowTextLength(h);
                    if (len > 0)
                    {
                        found = h;
                        return false; // Stop enumeration
                    }
                }
                return true;
            }, IntPtr.Zero);

            return found;
        }

        /// <summary>
        /// Force a window to the foreground using AttachThreadInput to bypass SetForegroundWindow restrictions.
        /// </summary>
        private void ForceForegroundWindow(IntPtr targetHwnd)
        {
            if (targetHwnd == IntPtr.Zero) return;

            // Restore window if minimized
            ShowWindowAsync(targetHwnd, SW_RESTORE);
            System.Threading.Thread.Sleep(100);

            // Retry: SetForegroundWindow can be rejected, so verify and try again
            for (int i = 0; i < 3; i++)
            {
                // If target is already foreground, done
                IntPtr fgHwnd = GetForegroundWindow();
                if (fgHwnd == targetHwnd) return;

                uint fgThreadId = GetWindowThreadProcessId(fgHwnd, out _);
                uint targetThreadId = GetWindowThreadProcessId(targetHwnd, out _);
                uint currentThreadId = GetCurrentThreadId();

                // Attach current thread's input to the foreground and target threads
                bool attachedFg = fgThreadId != 0 && fgThreadId != currentThreadId
                    && AttachThreadInput(fgThreadId, currentThreadId, true);
                bool attachedTarget = targetThreadId != 0 && targetThreadId != currentThreadId && targetThreadId != fgThreadId
                    && AttachThreadInput(targetThreadId, currentThreadId, true);

                try
                {
                    BringWindowToTop(targetHwnd);
                    SetForegroundWindow(targetHwnd);
                }
                finally
                {
                    if (attachedTarget)
                    {
                        AttachThreadInput(targetThreadId, currentThreadId, false);
                    }
                    if (attachedFg)
                    {
                        AttachThreadInput(fgThreadId, currentThreadId, false);
                    }
                }

                System.Threading.Thread.Sleep(100);
            }
        }
    }
}
