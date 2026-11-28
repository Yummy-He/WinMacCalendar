using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace WinMacCalendar;

internal static class Desktop
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_DISABLED = 0x08000000;
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_NOSENDCHANGING = 0x0400;
    private const uint ZPOS_FLAGS = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING;
    private const uint GW_HWNDNEXT = 2;
    private const uint GW_HWNDPREV = 3;
    private const int SW_SHOWNOACTIVATE = 4;
    private static readonly nint HWND_BOTTOM = 1;
    private static readonly nint HWND_TOPMOST = -1;
    private static readonly nint HWND_NOTOPMOST = -2;

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private const int DWMWA_EXCLUDED_FROM_PEEK = 12;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWCP_ROUND = 2;

    private static readonly HashSet<nint> TrackedWidgets = [];
    private static HwndSource? bottomAnchorSource;
    private static HwndSource? helperSource;
    private static nint bottomAnchorHwnd;
    private static nint helperHwnd;
    private static bool showDesktop;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public nint hwnd;
        public nint hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    private delegate void WinEventProc(nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetStyle(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetStyle(nint hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowExW(nint hwndParent, nint hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint hwnd, [Out] char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc, WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hWinEventHook);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(nint hProcess, nint dwMinimumWorkingSetSize, nint dwMaximumWorkingSetSize);

    private static readonly char[] ClassNameBuffer = new char[32];

    public static bool IsShowDesktop => showDesktop;

    public static void TrimMemory()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
            GC.WaitForPendingFinalizers();
            _ = SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
        }
        catch { }
    }

    public static void BringToForeground(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd != nint.Zero) SetForegroundWindow(hwnd);
    }

    private static void EnsureHelperWindows()
    {
        if (bottomAnchorSource == null || bottomAnchorHwnd == nint.Zero || !IsWindow(bottomAnchorHwnd))
        {
            bottomAnchorSource?.Dispose();
            bottomAnchorSource = new HwndSource(new HwndSourceParameters("WinMacCalendarBottomAnchor")
            {
                WindowStyle = unchecked((int)(WS_POPUP | WS_DISABLED)),
                ExtendedWindowStyle = WS_EX_TOOLWINDOW,
                Width = 0,
                Height = 0
            });
            bottomAnchorSource.AddHook(BlockExternalZOrderHook);
            bottomAnchorHwnd = bottomAnchorSource.Handle;
            SetWindowPos(bottomAnchorHwnd, HWND_BOTTOM, 0, 0, 0, 0, ZPOS_FLAGS);
        }

        if (helperSource == null || helperHwnd == nint.Zero || !IsWindow(helperHwnd))
        {
            helperSource?.Dispose();
            helperSource = new HwndSource(new HwndSourceParameters("WinMacCalendarLayerHelper")
            {
                WindowStyle = unchecked((int)(WS_POPUP | WS_DISABLED)),
                ExtendedWindowStyle = WS_EX_TOOLWINDOW,
                Width = 0,
                Height = 0
            });
            helperSource.AddHook(BlockExternalZOrderHook);
            helperHwnd = helperSource.Handle;
            SetWindowPos(helperHwnd, HWND_BOTTOM, 0, 0, 0, 0, ZPOS_FLAGS);
        }
    }

    public static void Attach(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == nint.Zero) return;

        EnsureHelperWindows();
        TrackedWidgets.Add(hwnd);
        w.Closed += (_, _) => TrackedWidgets.Remove(hwnd);

        SetStyle(hwnd, GWL_EXSTYLE, GetStyle(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
        try
        {
            int excludeFromPeek = 1;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_EXCLUDED_FROM_PEEK, ref excludeFromPeek, sizeof(int));
        }
        catch { }

        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(BlockExternalZOrderHook);
        Lower(w);
    }

    public static IDisposable WatchDesktopSwitch(Action<bool> onShowDesktopChanged)
    {
        EnsureHelperWindows();
        WinEventProc callback = (_, eventType, hwnd, _, _, _, _) =>
        {
            if (eventType != EVENT_SYSTEM_FOREGROUND) return;
            nint desktopHost = FindDesktopIconsHostWindow();
            if (!showDesktop)
            {
                if (hwnd != nint.Zero && (hwnd == desktopHost || IsDesktopShellWindow(hwnd, desktopHost)))
                {
                    // Explorer may raise the desktop window a few ms after EVENT_SYSTEM_FOREGROUND.
                    for (int i = 0; i < 6; i++)
                    {
                        if (CheckDesktopState(onShowDesktopChanged)) break;
                        System.Threading.Thread.Sleep(4);
                    }
                }
            }
            else
            {
                CheckDesktopState(onShowDesktopChanged);
            }
        };

        nint fgHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            nint.Zero,
            callback,
            0,
            0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        return new DesktopHookHandle(callback, fgHook);
    }

    private sealed class DesktopHookHandle(WinEventProc callback, nint fgHook) : IDisposable
    {
        private WinEventProc? keepAlive = callback;
        private nint hook = fgHook;

        public void Dispose()
        {
            if (hook != nint.Zero) { _ = UnhookWinEvent(hook); hook = nint.Zero; }
            keepAlive = null;
            showDesktop = false;
            bottomAnchorSource?.Dispose();
            bottomAnchorSource = null;
            bottomAnchorHwnd = nint.Zero;
            helperSource?.Dispose();
            helperSource = null;
            helperHwnd = nint.Zero;
        }
    }

    private static bool IsDesktopShellWindow(nint hwnd, nint desktopHost)
    {
        if (hwnd == nint.Zero) return false;
        int len = GetClassNameW(hwnd, ClassNameBuffer, ClassNameBuffer.Length);
        if (len <= 0) return false;
        ReadOnlySpan<char> span = ClassNameBuffer.AsSpan(0, len);
        if (!span.SequenceEqual("Progman") && !span.SequenceEqual("WorkerW")) return false;
        if (desktopHost == nint.Zero) return true;
        _ = GetWindowThreadProcessId(desktopHost, out uint desktopPid);
        _ = GetWindowThreadProcessId(hwnd, out uint hwndPid);
        return desktopPid == 0 || hwndPid == desktopPid;
    }

    private static nint FindDesktopIconsHostWindow()
    {
        nint shellW = GetShellWindow();
        if (shellW == nint.Zero) return nint.Zero;

        // Windows 11 24H2+ (and fresh Win10) hosts SHELLDLL_DefView directly under Progman.
        if (FindWindowExW(shellW, nint.Zero, "SHELLDLL_DefView", "") != nint.Zero)
            return shellW;

        // Pre-24H2 Windows 10/11 moves SHELLDLL_DefView into a WorkerW owned by explorer.exe.
        _ = GetWindowThreadProcessId(shellW, out uint shellPid);
        nint workerW = nint.Zero;
        while ((workerW = FindWindowExW(nint.Zero, workerW, "WorkerW", "")) != nint.Zero)
        {
            if (!IsWindowVisible(workerW)) continue;
            _ = GetWindowThreadProcessId(workerW, out uint pid);
            if (pid != shellPid) continue;
            if (FindWindowExW(workerW, nint.Zero, "SHELLDLL_DefView", "") != nint.Zero)
                return workerW;
        }
        return shellW;
    }

    private static bool IsAnchorBelowDesktop(nint desktopHost)
    {
        if (desktopHost == nint.Zero || bottomAnchorHwnd == nint.Zero || !IsWindowVisible(desktopHost))
            return false;

        nint cur = GetWindow(desktopHost, GW_HWNDNEXT);
        int guard = 0;
        while (cur != nint.Zero && guard++ < 2048)
        {
            if (cur == bottomAnchorHwnd) return true;
            cur = GetWindow(cur, GW_HWNDNEXT);
        }
        return false;
    }

    private static void PrepareHelperWindow(nint desktopHost)
    {
        EnsureHelperWindows();
        SetWindowPos(bottomAnchorHwnd, HWND_BOTTOM, 0, 0, 0, 0, ZPOS_FLAGS);

        if (showDesktop && desktopHost != nint.Zero)
        {
            // Promote helper to the topmost band, then tuck it immediately behind the backmost
            // topmost window (below Taskbar / Start Menu / ContextMenus, above the raised desktop).
            SetWindowPos(helperHwnd, HWND_TOPMOST, 0, 0, 0, 0, ZPOS_FLAGS);
            nint cur = desktopHost;
            int guard = 0;
            while ((cur = GetWindow(cur, GW_HWNDPREV)) != nint.Zero && guard++ < 512)
            {
                if (cur == helperHwnd || cur == bottomAnchorHwnd || TrackedWidgets.Contains(cur)) continue;
                if ((GetStyle(cur, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0)
                {
                    if (SetWindowPos(helperHwnd, cur, 0, 0, 0, 0, ZPOS_FLAGS))
                        return;
                }
            }
        }
        else
        {
            SetWindowPos(helperHwnd, HWND_NOTOPMOST, 0, 0, 0, 0, ZPOS_FLAGS);
            SetWindowPos(helperHwnd, HWND_BOTTOM, 0, 0, 0, 0, ZPOS_FLAGS);
        }
    }

    public static bool CheckDesktopState(Action<bool>? onShowDesktopChanged = null)
    {
        EnsureHelperWindows();
        nint desktopHost = FindDesktopIconsHostWindow();
        bool anchorBelow = IsAnchorBelowDesktop(desktopHost);
        bool stateChanged = anchorBelow != showDesktop;

        if (stateChanged)
        {
            showDesktop = anchorBelow;
            PrepareHelperWindow(desktopHost);
            ApplyLayerToAllTracked();
            onShowDesktopChanged?.Invoke(showDesktop);
        }
        return stateChanged;
    }

    private static void ApplyLayerToAllTracked()
    {
        foreach (var hwnd in TrackedWidgets)
        {
            if (!IsWindow(hwnd)) continue;
            ApplyLayer(hwnd);
        }
    }

    private static void ApplyLayer(nint hwnd)
    {
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);

        if (showDesktop && helperHwnd != nint.Zero && IsWindow(helperHwnd))
        {
            // Match helper's topmost band if needed, then insert directly behind helper
            // (strictly above raised desktop, strictly below Taskbar / Start Menu / ContextMenus).
            bool helperTopmost = (GetStyle(helperHwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
            if (helperTopmost && (GetStyle(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) == 0)
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, ZPOS_FLAGS);
            SetWindowPos(hwnd, helperHwnd, 0, 0, 0, 0, ZPOS_FLAGS);
        }
        else
        {
            if ((GetStyle(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0)
                SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, ZPOS_FLAGS);
            SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, ZPOS_FLAGS);
        }
    }

    private static nint BlockExternalZOrderHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_WINDOWPOSCHANGING && lParam != nint.Zero)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            pos.flags |= SWP_NOZORDER;
            Marshal.StructureToPtr(pos, lParam, false);
        }
        return nint.Zero;
    }

    public static void Lower(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == nint.Zero) return;
        EnsureHelperWindows();
        if (showDesktop)
            PrepareHelperWindow(FindDesktopIconsHostWindow());
        ApplyLayer(hwnd);
    }

    public static void StyleDialog(Window w, bool dark, Color? captionColor = null)
    {
        void Apply()
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == nint.Zero) return;
            try
            {
                int useDark = dark ? 1 : 0;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
                int corner = DWMWCP_ROUND;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
                if (captionColor is { } c)
                {
                    int colorRef = c.R | (c.G << 8) | (c.B << 16);
                    _ = DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorRef, sizeof(int));
                }
            }
            catch
            {
                // Older Windows 10 builds do not support Win11 DWM attributes.
            }
        }

        if (new WindowInteropHelper(w).Handle != nint.Zero) Apply();
        else w.SourceInitialized += (_, _) => Apply();
    }
}

internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "WinMacCalendar";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(AppName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            string? exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exe))
                key.SetValue(AppName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }
}
