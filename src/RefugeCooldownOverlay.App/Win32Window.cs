using System.Runtime.InteropServices;

namespace RefugeCooldownOverlay.App;

/// <summary>Win32 window helpers for the layered, click-through overlay. No input hooks.</summary>
internal static class Win32Window
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int MA_NOACTIVATE = 3;

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    public const int GW_OWNER = 4;

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    /// <summary>
    /// First visible, unowned top-level window of the given process, or IntPtr.Zero.
    /// Enumeration only — never activation, never input.
    /// </summary>
    public static IntPtr FindMainWindow(int processId)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var owner);
            if (owner == (uint)processId
                && IsWindowVisible(hWnd)
                && GetWindow(hWnd, GW_OWNER) == IntPtr.Zero)
            {
                found = hWnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>Apply or remove click-through + non-activating styles.</summary>
    public static void SetClickThrough(IntPtr handle, bool clickThrough)
    {
        var style = GetWindowLong(handle, GWL_EXSTYLE);
        style |= WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        if (clickThrough)
        {
            style |= WS_EX_TRANSPARENT;
        }
        else
        {
            style &= ~WS_EX_TRANSPARENT;
        }

        SetWindowLong(handle, GWL_EXSTYLE, style);
    }
}
