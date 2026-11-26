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
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly nint HWND_BOTTOM = 1;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWCP_ROUND = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public nint hwnd;
        public nint hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetStyle(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetStyle(nint hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(nint hProcess, nint dwMinimumWorkingSetSize, nint dwMaximumWorkingSetSize);

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

    public static void Attach(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        SetStyle(hwnd, GWL_EXSTYLE, GetStyle(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(KeepAtBottomHook);
        Lower(w);
    }

    private static nint KeepAtBottomHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_WINDOWPOSCHANGING && lParam != nint.Zero)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if (pos.hwndInsertAfter != HWND_BOTTOM)
            {
                pos.hwndInsertAfter = HWND_BOTTOM;
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }
        return nint.Zero;
    }

    public static void Lower(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd != nint.Zero)
            SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
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
