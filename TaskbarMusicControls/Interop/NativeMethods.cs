
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

}
