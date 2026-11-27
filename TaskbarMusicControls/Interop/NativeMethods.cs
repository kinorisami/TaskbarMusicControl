
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarMusicControls.Interop;

public static class NativeMethods
{
    public delegate bool EnumWindowsProc(
        IntPtr hwnd,
        IntPtr lParam
    );

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(
        EnumWindowsProc callback,
        IntPtr lParam
    );

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(
        IntPtr hwnd,
        StringBuilder className,
        int maxCount
    );

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(
        IntPtr hwnd
    );

    /// <summary>ShowWindow() command that restores a minimized window.</summary>
    public const int SwRestore = 9;

    /// <summary>ShowWindow() command that shows a hidden window.</summary>
    public const int SwShow = 5;

    /// <summary>ShowWindow() command that hides a window (what a player does when it goes to the tray).</summary>
    public const int SwHide = 0;

    /// <summary>GetWindow() flag for the owner window.</summary>
    public const uint GwOwner = 4;

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(
        IntPtr hwnd,
        out uint processId);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(
        IntPtr hwnd,
        uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(
        IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(
        IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly long Area =>
            (long)Math.Max(0, Right - Left) * Math.Max(0, Bottom - Top);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(
        IntPtr hwnd,
        out WindowRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(
        IntPtr hwnd
    );

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(
        IntPtr hwnd,
        int command
    );

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(
        IntPtr hwnd
    );
}
