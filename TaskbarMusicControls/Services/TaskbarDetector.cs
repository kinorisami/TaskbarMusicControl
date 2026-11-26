using System.Text;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Windows;
using TaskbarMusicControls.Interop;

namespace TaskbarMusicControls.Services;

internal sealed record TaskbarInfo(
    IntPtr Handle,
    Rect Bounds,
    string DeviceId,
    string DisplayName,
    bool IsPrimary);

internal sealed record TaskbarOption(string DeviceId, string DisplayName, bool IsPrimary);

internal sealed class TaskbarDetector
{
    private const int MonitorDefaultToNearest = 2;
    private const int MonitorDefaultToPrimary = 1;

    // Display names never change while the machine is running, so the
    // (comparatively expensive) display device lookup is done once per device.
    private static readonly ConcurrentDictionary<string, string> DisplayNameCache =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<TaskbarInfo> DetectTaskbars()
    {
        var primaryMonitor = MonitorFromPoint(new NativePoint(0, 0), MonitorDefaultToPrimary);
        var results = new List<TaskbarInfo>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            var className = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, className, className.Capacity);
            if (!IsTaskbarClassName(className.ToString()))
                return true;

            if (!GetWindowRect(hwnd, out var rect))
                return true;

            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            var monitorInfo = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            var deviceId = GetMonitorInfo(monitor, ref monitorInfo)
                ? monitorInfo.DeviceName
                : "";
            results.Add(new TaskbarInfo(
                hwnd,
                new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top),
                deviceId,
                GetDisplayName(deviceId),
                monitor != 0 && monitor == primaryMonitor));
            return true;
        }, IntPtr.Zero);

        return results;
    }

    internal static bool IsTaskbarClassName(string className) =>
        className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    /// <summary>
    /// Cheap check that only looks at the class names of the top level windows.
    /// Used to poll whether the taskbar became enumerable again without paying
    /// for the full monitor bookkeeping of <see cref="DetectTaskbars"/>.
    /// </summary>
    public bool HasTaskbarWindow()
    {
        var found = false;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            var className = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, className, className.Capacity);
            if (!IsTaskbarClassName(className.ToString()))
                return true;

            found = true;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    private static string GetDisplayName(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return "Unknown display";

        if (DisplayNameCache.TryGetValue(deviceId, out var cached))
            return cached;

        var display = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        var name = EnumDisplayDevices(deviceId, 0, ref display, 0) &&
                   !string.IsNullOrWhiteSpace(display.DeviceString)
            ? display.DeviceString
            : deviceId;
        DisplayNameCache[deviceId] = name;
        return name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect(int left, int top, int right, int bottom)
    {
        public int Left = left;
        public int Top = top;
        public int Right = right;
        public int Bottom = bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public uint StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx monitorInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(
        string? device,
        uint deviceIndex,
        ref DisplayDevice displayDevice,
        uint flags);
}
