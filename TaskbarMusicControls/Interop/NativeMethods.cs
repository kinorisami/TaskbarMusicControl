
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
