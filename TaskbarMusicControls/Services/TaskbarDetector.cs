using System.Text;
using TaskbarMusicControls.Interop;

namespace TaskbarMusicControls.Services;

internal sealed class TaskbarDetector
{
    public IReadOnlyList<IntPtr> DetectTaskbars()
    {
        var results = new List<IntPtr>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            var className = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, className, className.Capacity);
            if (IsTaskbarClassName(className.ToString()))
                results.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        return results;
    }

    internal static bool IsTaskbarClassName(string className) =>
        className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
}
