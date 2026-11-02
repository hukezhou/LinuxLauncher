using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace vllmLauncher.Services
{
    /// <summary>
    /// Executes commands by opening PowerShell windows (connects to remote server via SSH in PowerShell)
    /// </summary>
    public class PowerShellService
    {
        /// <summary>
        /// Serializes the "ensure foreground → clipboard → SendInput" paste phase.
        /// Foreground window, clipboard and SendInput are global resources: two concurrent
        /// delay→paste flows would steal foreground/clipboard from each other and at least
        /// one target window would silently receive nothing.
        /// </summary>
        private static readonly SemaphoreSlim _pasteLock = new SemaphoreSlim(1, 1);

        #region Win32 P/Invoke

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
        [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint dwProcessId);
        [DllImport("kernel32.dll")] private static extern bool FreeConsole();
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc ewProc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
        private const int SW_RESTORE = 9;
        [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll")] private static extern bool EmptyClipboard();
        [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint uFlags, IntPtr dwBytes);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
        private const uint CF_UNICODETEXT = 13;
        private const uint GHND = 0x42;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public int time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public int time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT { public int uMsg; public short wParamL; public short wParamH; }
        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public HARDWAREINPUT hi; }
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public INPUTUNION union; }

        [DllImport("user32.dll")] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        private const int KEYEVENTF_KEYUP = 0x0002;
        private const int KEYEVENTF_UNICODE = 0x0004;
        private const uint INPUT_KEYBOARD = 1;

        #endregion

        /// <summary>
        /// Opens a PowerShell window and connects to the remote server via SSH
        /// </summary>
        public void ConnectToRemote(string serverAddress)
        {
            string cmd = $"ssh {serverAddress}";
            StartPowerShell(cmd, keepOpen: true);
        }

        /// <summary>
        /// 1) 打开PowerShell，SSH登录
        /// 2) 获取窗口句柄
        /// 3) Timer延时：脚本复制到剪贴板 → Ctrl+V粘贴 → 延时200ms → Enter
        ///    （useScreen时：粘贴前先逐字符键入screen会话，发送后延时5秒再发 Ctrl+A + d 转入后台）
        /// </summary>
        public void StartWithDelay(string serverAddress, string scriptText, bool useScreen, double pasteWaitTime = 3.0)
        {
            // 1) 打开PowerShell，执行SSH（ssh命令在进程命令行里，无需键入）
            Process? psProcess = StartPowerShellReturnProcess($"ssh {serverAddress}", keepOpen: true);
            int targetPid = psProcess?.Id ?? 0;

            // 2) Timer延时后执行粘贴。粘贴阶段全局串行（见 _pasteLock），
            //    多个执行流并发时排队粘贴，避免互相抢前台焦点/剪贴板导致命令丢失。
            var stepTimer = new System.Timers.Timer(pasteWaitTime * 1000) { AutoReset = false };
            stepTimer.Elapsed += async (s, e) =>
            {
                try { await _pasteLock.WaitAsync().ConfigureAwait(false); }
                catch { return; }
                try
                {
                    // 定位目标控制台窗口。控制台可能由经典conhost承载（AttachConsole能拿到真窗口），
                    // 也可能由 Windows Terminal/ConPTY 承载：窗口属于 WindowsTerminal 进程，
                    // 控制台标题会传播成标签页标题 —— 用 ssh 目标（如 myuser@ 前缀）匹配标题：
                    // 连接中标题是 "ssh myuser@[...]"，登录后是 "myuser@<远端主机名>"，都能命中。
                    string raw = serverAddress.Trim();
                    int sp = raw.IndexOf(' ');
                    string sshTarget = sp > 0 ? raw.Substring(0, sp) : raw;   // 如 myuser@[2001:db8::1]
                    int at = sshTarget.IndexOf('@');
                    string userHint = at > 0 ? sshTarget.Substring(0, at + 1) : sshTarget; // 如 myuser@

                    IntPtr hwnd = IntPtr.Zero;
                    bool byTitle = false;
                    // SSH 登录较慢时 WT 标签页标题要等登录完成才变成 "myuser@..."，轮询等待
                    DateTime deadline = DateTime.UtcNow.AddSeconds(8);
                    while (true)
                    {
                        hwnd = TryFindConsoleWindow(targetPid, sshTarget, userHint, out byTitle);
                        if (hwnd != IntPtr.Zero || DateTime.UtcNow >= deadline) break;
                        System.Threading.Thread.Sleep(200);
                    }
                    if (hwnd == IntPtr.Zero)
                    {
                        // 兜底：powershell PID 自己的可见窗口 / 旧式"PowerShell"标题控制台
                        if (targetPid > 0) hwnd = FindWindowByPid(targetPid);
                        if (hwnd == IntPtr.Zero) hwnd = FindPowerShellConsole();
                    }

                    // 强制取得目标窗口的前台焦点（重试直到生效），确保后续粘贴按键进入正确窗口
                    EnsureForegroundWindow(hwnd);
                    IntPtr fg = GetForegroundWindow();
                    // 安全闸门：目标拿不到前台、或目标按标题匹配但标题已变（用户在等待期间
                    // 切到了别的标签页）时宁可跳过，也不把命令敲进别的窗口
                    bool titleStillOurs = true;
                    if (byTitle)
                    {
                        string t = WindowTitle(hwnd);
                        titleStillOurs = t.Contains(userHint, StringComparison.OrdinalIgnoreCase) ||
                                         t.Contains(sshTarget, StringComparison.OrdinalIgnoreCase);
                    }
                    bool safePaste = hwnd != IntPtr.Zero && fg == hwnd && titleStillOurs;
                    if (!safePaste) return; // 宁可跳过，也不把命令敲进别的窗口
                    System.Threading.Thread.Sleep(100);

                    // a) 如果useScreen为true，先逐字符键入screen命令（前台确认+注入重试+整行重发）。
                    //    两轮都没能完整键入则放弃本次执行：宁可不发，也不把残命令按Enter跑起来。
                    if (useScreen)
                    {
                        if (!SendCommandReliable("screen -S qwen", hwnd)) return;
                        System.Threading.Thread.Sleep(100);
                        SendVkInput(0x0D); // ENTER
                        System.Threading.Thread.Sleep(2000);
                    }

                    // b) 脚本内容复制到剪贴板（Win32 API，不依赖STA线程）
                    SetClipboardTextWin32(scriptText);

                    // c) Ctrl+V 粘贴（组合键一次入队，见 SendCtrlCombo）
                    if (!SendCtrlCombo(0x56, hwnd)) return; // V

                    // d) 延时200ms后发送Enter
                    System.Threading.Thread.Sleep(200);
                    SendVkInput(0x0D); // ENTER

                    // e) 当useScreen开启时，延时5秒后发screen的detach命令：Ctrl+A 再跟裸 d。
                    //    screen默认绑定是 "C-a d, C-a C-d (detach)"：两种写法都有效，
                    //    但裸 d 省掉第二个Ctrl —— 少一次修饰键切换，就少一次
                    //    “字母丢掉Ctrl、变成普通字符打进shell”的风险（提示符上残'd'的来源）。
                    if (useScreen)
                    {
                        System.Threading.Thread.Sleep(5000);
                        // 这5秒里前台可能已被别的窗口抢走：先重新抢回目标窗口并确认，
                        // 否则按键打进别的窗口，screen 收不到命令，字符还落进别人的输入行。
                        EnsureForegroundWindow(hwnd);
                        if (GetForegroundWindow() != hwnd) return;

                        if (!SendCtrlCombo(0x41, hwnd)) return; // Ctrl+A：screen 的命令字符
                        System.Threading.Thread.Sleep(200);      // 给命令模式留出切换时间
                        SendCharReliable('d', hwnd);             // d：detach（Unicode注入，带前台确认+重试）
                    }
                }
                catch { }
                finally { _pasteLock.Release(); }
            };
            stepTimer.Start();
        }

        /// <summary>查找最近的PowerShell控制台窗口</summary>
        private IntPtr FindPowerShellConsole()
        {
            IntPtr console = GetConsoleWindow();
            if (console != IntPtr.Zero) return console;

            
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                int len = GetWindowTextLength(h);
                if (len > 0)
                {
                    var sb = new StringBuilder(len);
                    GetWindowText(h, sb, len + 1);
                    string title = sb.ToString();
                    if (title.Contains("PowerShell") || title.Contains("Windows PowerShell"))
                    {
                        uint pid;
                        GetWindowThreadProcessId(h, out pid);
                        try { var p = Process.GetProcessById((int)pid); if (p.ProcessName.Contains("powershell") || p.ProcessName.Contains("cmd")) { found = h; return false; } } catch { }
                    }
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>通过进程ID查找可见窗口句柄（过滤掉宿主控制台留下的不可见幽灵窗口）</summary>
        private IntPtr FindWindowByPid(int targetPid)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid == (uint)targetPid && IsWindowVisible(h) && GetWindowTextLength(h) > 0)
                {
                    found = h;
                    return false; // Stop enumeration
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>当前所有"像正常窗口"的可见顶层窗口（有标题、尺寸足够），按Z序</summary>
        private static List<IntPtr> EnumVisibleTopWindows()
        {
            var list = new List<IntPtr>();
            EnumWindows((h, l) =>
            {
                if (IsWindowVisible(h) && GetWindowTextLength(h) > 0 &&
                    GetWindowRect(h, out RECT r) && (r.Right - r.Left) >= 200 && (r.Bottom - r.Top) >= 120)
                {
                    list.Add(h);
                }
                return true;
            }, IntPtr.Zero);
            return list;
        }

        private static string WindowTitle(IntPtr hWnd)
        {
            int len = GetWindowTextLength(hWnd);
            if (len == 0) return string.Empty;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, len + 1);
            return sb.ToString();
        }

        /// <summary>
        /// 查找承载指定 powershell 进程的控制台窗口：
        /// 1) 经典 conhost 承载时 AttachConsole+GetConsoleWindow 能拿到真实可见窗口；
        /// 2) Windows Terminal/ConPTY 承载时窗口属于 WindowsTerminal 进程，控制台标题会传播为
        ///    标签页标题，按 ssh 目标（原始地址或 "user@" 前缀）匹配标题；
        /// 3) 都找不到返回 Zero，由调用方兜底。
        /// </summary>
        private IntPtr TryFindConsoleWindow(int psPid, string sshTarget, string userHint, out bool byTitle)
        {
            byTitle = false;
            if (psPid > 0 && AttachConsole((uint)psPid))
            {
                IntPtr w = GetConsoleWindow();
                FreeConsole();
                // ConPTY(Windows Terminal 承载)时返回的是无标题伪窗口，永远拿不到前台，必须排除；
                // 经典 conhost 的窗口标题就是控制台标题，且尺寸正常，可以使用
                if (w != IntPtr.Zero && IsWindowVisible(w) && GetWindowTextLength(w) > 0 &&
                    GetWindowRect(w, out RECT wr) && (wr.Right - wr.Left) >= 200 && (wr.Bottom - wr.Top) >= 120)
                {
                    return w;
                }
            }
            foreach (var h in EnumVisibleTopWindows())
            {
                string t = WindowTitle(h);
                if (t.Contains(userHint, StringComparison.OrdinalIgnoreCase) ||
                    t.Contains(sshTarget, StringComparison.OrdinalIgnoreCase))
                {
                    byTitle = true;
                    return h;
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>使用AttachThreadInput强制将目标窗口设为前台，绕过SetForegroundWindow的限制（单次尝试）</summary>
        private void ForceForegroundWindow(IntPtr targetHwnd)
        {
            if (targetHwnd == IntPtr.Zero) return;

            // 如果目标已经是前台窗口，直接返回
            IntPtr fgHwnd = GetForegroundWindow();
            if (fgHwnd == targetHwnd) return;

            uint fgThreadId = GetWindowThreadProcessId(fgHwnd, out _);
            uint targetThreadId = GetWindowThreadProcessId(targetHwnd, out _);
            uint currentThreadId = GetCurrentThreadId();

            // 将当前线程的输入附加到前台窗口线程与目标窗口线程（两者都附加，成功率更高）
            bool attachedFg = false;
            bool attachedTarget = false;
            if (fgThreadId != 0 && fgThreadId != currentThreadId)
            {
                attachedFg = AttachThreadInput(fgThreadId, currentThreadId, true);
            }
            if (targetThreadId != 0 && targetThreadId != currentThreadId && targetThreadId != fgThreadId)
            {
                attachedTarget = AttachThreadInput(targetThreadId, currentThreadId, true);
            }

            try
            {
                BringWindowToTop(targetHwnd);
                SetForegroundWindow(targetHwnd);
            }
            finally
            {
                // 分离线程输入
                if (attachedTarget)
                {
                    AttachThreadInput(targetThreadId, currentThreadId, false);
                }
                if (attachedFg)
                {
                    AttachThreadInput(fgThreadId, currentThreadId, false);
                }
            }
        }

        /// <summary>重试强制取得目标窗口的前台焦点，直到 GetForegroundWindow()==目标窗口（确保粘贴按键进入正确窗口）</summary>
        private void EnsureForegroundWindow(IntPtr targetHwnd)
        {
            if (targetHwnd == IntPtr.Zero) return;

            for (int i = 0; i < 6; i++)
            {
                if (GetForegroundWindow() == targetHwnd) return;

                // 窗口若被最小化，先恢复
                ShowWindowAsync(targetHwnd, SW_RESTORE);
                System.Threading.Thread.Sleep(50);

                // 首次重试前发一次无害按键(Shift)：让系统认为本进程刚收到输入，
                // 解除前台锁定，使 SetForegroundWindow 生效
                if (i == 0) SendVkInput(0x10);

                ForceForegroundWindow(targetHwnd);
                System.Threading.Thread.Sleep(250);
            }
        }

        /// <summary>通过SendInput发送Unicode字符（逐字符，带前台确认与注入重试）。
        /// 间隔带随机抖动，适配慢速SSH的输入背压；任一字符彻底失败即返回false。</summary>
        private bool SendKeysUnicode(string text, IntPtr targetHwnd)
        {
            var rng = new Random();
            foreach (char c in text)
            {
                if (!SendCharReliable(c, targetHwnd)) return false;
                System.Threading.Thread.Sleep(120 + rng.Next(-25, 26)); // 120ms ± 25ms
            }
            return true;
        }

        /// <summary>发送单个Unicode字符：注入前确认目标在前台，SendInput未全部入队则重试。</summary>
        private bool SendCharReliable(char c, IntPtr targetHwnd)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (GetForegroundWindow() != targetHwnd)
                    ForceForegroundWindow(targetHwnd);

                var inputs = new INPUT[2];
                inputs[0] = CreateKeyInput(c, false);
                inputs[1] = CreateKeyInput(c, true);
                if (SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT))) == 2)
                    return true;

                System.Threading.Thread.Sleep(120);
            }
            return false;
        }

        /// <summary>
        /// 逐字符键入整条命令；若中途有字符彻底注入失败，发 Ctrl+C（取消当前输入行）
        /// 后整行重发一次。返回false表示两轮都没完整键入，调用方不应再按Enter。
        /// </summary>
        private bool SendCommandReliable(string text, IntPtr targetHwnd)
        {
            for (int round = 0; round < 2; round++)
            {
                if (round > 0)
                {
                    // 取消上一轮键入到一半的残行，再从头来过。
                    // 用Ctrl+C而非Ctrl+U：Ctrl+C在bash/PowerShell/cmd都能取消当前输入行，
                    // Ctrl+U只在bash(readline)有效，在PS控制台只是回显^U，清不掉残行。
                    SendCtrlCombo(0x43, targetHwnd); // Ctrl+C
                    System.Threading.Thread.Sleep(300);
                }
                if (SendKeysUnicode(text, targetHwnd))
                    return true;
            }
            return false;
        }

        /// <summary>原样发送脚本内容，\n\r 正常发送，最后再发一个Enter确保执行</summary>
        private void SendScriptWithNewline(string text, IntPtr targetHwnd)
        {
            SendKeysUnicode(text, targetHwnd);
            System.Threading.Thread.Sleep(100);
            SendVkInput(0x0D); // 全部发送完毕，最后发送一个Enter
        }

        /// <summary>发送虚拟键(Vk) 按下+释放</summary>
        private void SendVkInput(ushort vk)
        {
            var inputs = new INPUT[2];
            inputs[0] = new INPUT { type = INPUT_KEYBOARD, union = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk } } };
            inputs[1] = new INPUT { type = INPUT_KEYBOARD, union = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } } };
            SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        /// <summary>发送虚拟键按下（不释放）</summary>
        private void SendVkInputDown(ushort vk)
        {
            var input = new INPUT { type = INPUT_KEYBOARD, union = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk } } };
            SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }

        /// <summary>发送虚拟键释放</summary>
        private void SendVkInputUp(ushort vk)
        {
            var input = new INPUT { type = INPUT_KEYBOARD, union = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } } };
            SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }

        /// <summary>
        /// 发送 Ctrl+Vk 组合键：Ctrl↓、Vk↓、Vk↑、Ctrl↑ 四个事件放进同一次 SendInput。
        /// 分开调用时 Ctrl↓ 与字母↓ 之间是0间隔，字母容易丢掉Ctrl——后果不是“没反应”，
        /// 而是shell里凭空多出一个普通字符（组合键没生效、字符却进了输入行）。
        /// 注入前确认目标在前台，未全部入队则重试；彻底失败返回false。
        /// </summary>
        private bool SendCtrlCombo(ushort vk, IntPtr targetHwnd)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (GetForegroundWindow() != targetHwnd)
                    ForceForegroundWindow(targetHwnd);

                var inputs = new INPUT[4];
                inputs[0] = CreateVkInput(0x11, false); // Ctrl down
                inputs[1] = CreateVkInput(vk, false);    // 字母 down
                inputs[2] = CreateVkInput(vk, true);     // 字母 up
                inputs[3] = CreateVkInput(0x11, true);   // Ctrl up
                if (SendInput(4, inputs, Marshal.SizeOf(typeof(INPUT))) == 4)
                    return true;

                System.Threading.Thread.Sleep(120);
            }
            return false;
        }

        /// <summary>通过Win32 API写入剪贴板（不依赖STA线程）</summary>
        private void SetClipboardTextWin32(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            byte[] bytes = Encoding.Unicode.GetBytes(text);
            int byteCount = bytes.Length + 2; // null terminator
            IntPtr hGlobal = GlobalAlloc(GHND, (IntPtr)byteCount);
            if (hGlobal == IntPtr.Zero) return;
            try
            {
                IntPtr pGlobal = GlobalLock(hGlobal);
                if (pGlobal != IntPtr.Zero)
                {
                    Marshal.Copy(bytes, 0, pGlobal, bytes.Length);
                    Marshal.WriteByte(pGlobal, bytes.Length, 0);
                    Marshal.WriteByte(pGlobal, bytes.Length + 1, 0);
                    GlobalUnlock(hGlobal);
                }
                OpenClipboard(IntPtr.Zero);
                EmptyClipboard();
                SetClipboardData(CF_UNICODETEXT, hGlobal);
                CloseClipboard();
            }
            catch
            {
                CloseClipboard();
                throw;
            }
        }

        /// <summary>构造虚拟键INPUT（Vk注入，用于需要修饰键参与的组合键）</summary>
        private INPUT CreateVkInput(ushort vk, bool keyUp)
        {
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                union = new INPUTUNION
                {
                    ki = new KEYBDINPUT { wVk = vk, dwFlags = (uint)(keyUp ? KEYEVENTF_KEYUP : 0) }
                }
            };
        }

        private INPUT CreateKeyInput(char c, bool keyUp)
        {
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                union = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wScan = c,
                        dwFlags = (uint)(KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0))
                    }
                }
            };
        }

        private void StartPowerShell(string command, bool keepOpen)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = keepOpen ? $"-NoExit -Command \"{command}\"" : $"-Command \"{command}\"",
                UseShellExecute = true,
                CreateNoWindow = false
            };

            try
            {
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to start PowerShell: {ex.Message}", ex);
            }
        }

        /// <summary>打开PowerShell并返回Process对象，用于后续获取窗口句柄</summary>
        private Process? StartPowerShellReturnProcess(string command, bool keepOpen)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = keepOpen ? $"-NoExit -Command \"{command}\"" : $"-Command \"{command}\"",
                UseShellExecute = true,
                CreateNoWindow = false
            };

            try
            {
                return Process.Start(psi);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to start PowerShell: {ex.Message}", ex);
            }
        }

        private void StartPowerShellFromPs1(string ps1Path, bool keepOpen)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = keepOpen
                    ? $"-NoExit -ExecutionPolicy Bypass -File \"{ps1Path}\""
                    : $"-ExecutionPolicy Bypass -File \"{ps1Path}\"",
                UseShellExecute = true,
                CreateNoWindow = false
            };

            try
            {
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to start PowerShell: {ex.Message}", ex);
            }
        }

        private string WriteTempPs1(string content)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), $"vllm_{Guid.NewGuid():N}.ps1");
            File.WriteAllText(tempPath, content, Encoding.UTF8);
            return tempPath;
        }

        private string EscapePsString(string input)
        {
            // Escape single quotes for PowerShell single-quoted strings: ' -> ''
            return input.Replace("'", "''");
        }

        /// <summary>
        /// 打开PowerShell → SSH登录 → 查询vllm进程PID → 循环kill -9 → 退出所有qwen的screen会话
        /// </summary>
        public void KillAllVllmProcesses(string serverAddress)
        {
            string getPidCmd = "pgrep -if vllm";
            string killCmd = "for pid in $(pgrep -if vllm); do echo \"kill -9 $pid\"; kill -9 $pid; done";
            string screenListCmd = "screen -ls | grep qwen";
            string screenKillCmd = "screen -ls | grep qwen | awk '{print $1}' | xargs -r -I{} screen -X -S {} quit";

            // 1) 打开PowerShell，执行SSH
            StartPowerShell($"ssh {serverAddress}", keepOpen: true);

            // 2) 等待窗口打开并获取句柄
            System.Threading.Thread.Sleep(2000);
            IntPtr hwnd = FindPowerShellConsole();

            // 3) 恢复到前台
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);

            // 4) Timer延时2秒后执行
            var timer = new System.Timers.Timer(2000) { AutoReset = false };
            timer.Elapsed += (s, e) =>
            {
                // a) 查询vllm进程PID
                SetClipboardTextWin32(getPidCmd);
                System.Threading.Thread.Sleep(100);
                SendVkInputDown(0x11); SendVkInputDown(0x56);
                System.Threading.Thread.Sleep(50);
                SendVkInputUp(0x56); SendVkInputUp(0x11);
                System.Threading.Thread.Sleep(200);
                SendVkInput(0x0D); // ENTER

                // b) 循环kill每个PID
                System.Threading.Thread.Sleep(1000);
                SetClipboardTextWin32(killCmd);
                System.Threading.Thread.Sleep(100);
                SendVkInputDown(0x11); SendVkInputDown(0x56);
                System.Threading.Thread.Sleep(50);
                SendVkInputUp(0x56); SendVkInputUp(0x11);
                System.Threading.Thread.Sleep(200);
                SendVkInput(0x0D); // ENTER

                // c) 查询qwen相关的screen窗口
                System.Threading.Thread.Sleep(1000);
                SetClipboardTextWin32(screenListCmd);
                System.Threading.Thread.Sleep(100);
                SendVkInputDown(0x11); SendVkInputDown(0x56);
                System.Threading.Thread.Sleep(50);
                SendVkInputUp(0x56); SendVkInputUp(0x11);
                System.Threading.Thread.Sleep(200);
                SendVkInput(0x0D); // ENTER

                // d) 退出所有qwen的screen会话
                System.Threading.Thread.Sleep(1000);
                SetClipboardTextWin32(screenKillCmd);
                System.Threading.Thread.Sleep(100);
                SendVkInputDown(0x11); SendVkInputDown(0x56);
                System.Threading.Thread.Sleep(50);
                SendVkInputUp(0x56); SendVkInputUp(0x11);
                System.Threading.Thread.Sleep(200);
                SendVkInput(0x0D); // ENTER
            };
            timer.Start();
        }

        /// <summary>
        /// 打开PowerShell → SSH登录 → 查询vllm进程PID → 循环kill -9 → 退出所有qwen的screen会话
        /// </summary>
        public void KillAllVllmProcessesWithScreen(string serverAddress)
        {
            string getPidCmd = "pgrep -if vllm";
            string killCmd = "for pid in $(pgrep -if vllm); do echo \"kill -9 $pid\"; kill -9 $pid; done";
            string screenListCmd = "screen -ls | grep qwen";
            string screenKillCmd = "screen -ls | grep qwen | awk '{print $1}' | xargs -r -I{} screen -X -S {} quit";

            // 1) 打开PowerShell，执行SSH
            StartPowerShell($"ssh {serverAddress}", keepOpen: true);

            // 2) 等待窗口打开并获取句柄
            System.Threading.Thread.Sleep(2000);
            IntPtr hwnd = FindPowerShellConsole();

            // 3) 恢复到前台
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);

            // 4) Timer延时2秒后执行
            var timer = new System.Timers.Timer(2000) { AutoReset = false };
            timer.Elapsed += (s, e) =>
            {
                // a) 查询vllm进程PID
                SetClipboardTextWin32(getPidCmd);
                System.Threading.Thread.Sleep(100);
                SendVkInputDown(0x11); SendVkInputDown(0x56);
                System.Threading.Thread.Sleep(50);
                SendVkInputUp(0x56); SendVkInputUp(0x11);
                System.Threading.Thread.Sleep(200);
                SendVkInput(0x0D); // ENTER

                // b) 循环kill每个PID
                System.Threading.Thread.Sleep(1000);
                SetClipboardTextWin32(killCmd);
                System.Threading.Thread.Sleep(100);
                SendVkInputDown(0x11); SendVkInputDown(0x56);
                System.Threading.Thread.Sleep(50);
                SendVkInputUp(0x56); SendVkInputUp(0x11);
                System.Threading.Thread.Sleep(200);
                SendVkInput(0x0D); // ENTER

                // c) 查询screen会话
                System.Threading.Thread.Sleep(1000);
                SetClipboardTextWin32(screenListCmd);
                System.Threading.Thread.Sleep(100);
                SendVkInputDown(0x11); SendVkInputDown(0x56);
                System.Threading.Thread.Sleep(50);
                SendVkInputUp(0x56); SendVkInputUp(0x11);
                System.Threading.Thread.Sleep(200);
                SendVkInput(0x0D); // ENTER

                // d) 退出所有名字带qwen的screen会话
                System.Threading.Thread.Sleep(1000);
                SetClipboardTextWin32(screenKillCmd);
                System.Threading.Thread.Sleep(100);
                SendVkInputDown(0x11); SendVkInputDown(0x56);
                System.Threading.Thread.Sleep(50);
                SendVkInputUp(0x56); SendVkInputUp(0x11);
                System.Threading.Thread.Sleep(200);
                SendVkInput(0x0D); // ENTER
            };
            timer.Start();
        }
    }
}
