using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace TaskbarMusicControls.Services;

/// <summary>
/// The notification-area icon. It exists so the widget still has an entry point
/// when the taskbar has no room for it, or when it is hidden because nothing is
/// playing; the menu it opens is the widget's own context menu.
/// </summary>
/// <remarks>
/// Implemented on a message-only window rather than a WinForms NotifyIcon: the
/// project has no WinForms reference, and the widget already talks to Win32
/// directly. The icon has to be added again when Explorer restarts, which the
/// registered "TaskbarCreated" message announces.
/// </remarks>
internal sealed class TrayIcon : IDisposable
{
    private const int WmApp = 0x8000;
    private const int WmTrayIcon = WmApp + 1;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NifMessage = 0x01;
    private const uint NifIcon = 0x02;
    private const uint NifTip = 0x04;
    private const int IdiApplication = 32512;
    private const int TrayIconId = 1;
    private const string Tooltip = "Taskbar Music Widget";

    private readonly HwndSource _source;
    private readonly uint _taskbarCreatedMessage;
    private nint _icon;
    private bool _disposed;

    /// <summary>Raised by a right click, with the pointer's screen position.</summary>
    public event Action<int, int>? MenuRequested;

    /// <summary>
    /// Raised by a left click. It toggles the widget instead of opening the
    /// menu: that is the control a tray icon normally offers, and it is the way
    /// back to a widget the user hid by hand.
    /// </summary>
    public event Action? ToggleRequested;

    /// <summary>The message window; the menu uses it as its owner.</summary>
    public nint Handle { get; }

    public TrayIcon()
    {
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        var parameters = new HwndSourceParameters("TaskbarMusicControlsTray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0x00000080, // WS_EX_TOOLWINDOW
            ParentWindow = new nint(-3)       // HWND_MESSAGE
        };
        _source = new HwndSource(parameters);
        Handle = _source.Handle;
        _source.AddHook(WndProc);

        _icon = LoadAppIcon();
        AddIcon();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        RemoveIcon();
        if (_icon != 0)
        {
            DestroyIcon(_icon);
            _icon = 0;
        }

        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmTrayIcon)
        {
            // lParam carries the mouse message that happened on the icon.
            var mouseMessage = (int)(lParam.ToInt64() & 0xFFFF);
            if (mouseMessage == WmLButtonUp)
            {
                handled = true;
                ToggleRequested?.Invoke();
            }
            else if (mouseMessage is WmRButtonUp or WmContextMenu)
            {
                handled = true;
                if (GetCursorPos(out var cursor))
                    MenuRequested?.Invoke(cursor.X, cursor.Y);
            }

            return 0;
        }

        if ((uint)message == _taskbarCreatedMessage)
        {
            // Explorer restarted: the icon went with it.
            _added = false;
            AddIcon();
        }

        return 0;
    }

    private bool _added;
    private int _addAttempts;
    private string _tooltip = Tooltip;

    /// <summary>
    /// The tooltip states why the widget is not on screen, which the widget
    /// itself cannot show while it is hidden.
    /// </summary>
    public void SetTooltip(string text)
    {
        if (string.Equals(_tooltip, text, StringComparison.Ordinal))
            return;

        _tooltip = text;
        if (!_added)
            return;

        var data = CreateData();
        data.uFlags = NifTip;
        data.szTip = text;
        Shell_NotifyIcon(NimModify, ref data);
    }

    /// <summary>
    /// Adds the icon again after Explorer dropped it. Called from the
    /// TaskbarCreated broadcast and from the taskbar watcher, whichever notices
    /// the restart first.
    /// </summary>
    public void ReAdd()
    {
        _added = false;
        _addAttempts = 0;
        AddIcon();
    }

    private void AddIcon()
    {
        if (_disposed || _added || Handle == 0)
            return;

        var data = CreateData();
        data.uFlags = NifMessage | NifIcon | NifTip;
        _added = Shell_NotifyIcon(NimAdd, ref data);
        TaskbarLayoutDiagnostics.Write($"tray icon added={_added}");
        if (_added)
        {
            _addAttempts = 0;
            return;
        }

        // Explorer may still be building its notification area when it announces
        // the restart; keep trying for a few seconds.
        if (++_addAttempts > 5)
            return;

        var dispatcher = _source?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;

        _ = Task.Delay(TimeSpan.FromSeconds(1)).ContinueWith(
            _ => dispatcher.BeginInvoke(new Action(AddIcon)),
            TaskScheduler.Default);
    }

    private void RemoveIcon()
    {
        if (!_added)
            return;

        var data = CreateData();
        Shell_NotifyIcon(NimDelete, ref data);
        _added = false;
    }

    private NotifyIconData CreateData() => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = Handle,
        uID = TrayIconId,
        uCallbackMessage = WmTrayIcon,
        hIcon = _icon,
        szTip = _tooltip,
        szInfo = "",
        szInfoTitle = ""
    };

    /// <summary>
    /// The icon embedded in the executable by ApplicationIcon. A missing icon
    /// would leave an invisible entry in the notification area, so the system's
    /// default icon is the fallback.
    /// </summary>
    private static nint LoadAppIcon()
    {
        try
        {
            var path = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(path) &&
                ExtractIconEx(path, 0, out var large, out var small, 1) > 0)
            {
                if (large != 0)
                    DestroyIcon(large);
                if (small != 0)
                    return small;
            }
        }
        catch (Exception)
        {
            // Fall through to the system icon.
        }

        return LoadIcon(0, new nint(IdiApplication));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string file, int index, out nint largeIcon, out nint smallIcon, uint count);

    [DllImport("user32.dll")]
    private static extern nint LoadIcon(nint instance, nint iconName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
