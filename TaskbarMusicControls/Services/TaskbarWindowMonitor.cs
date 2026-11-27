using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarMusicControls.Services;

internal sealed class TaskbarWindowMonitor : IDisposable
{
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;
    private const int ObjIdWindow = 0;
    private const int ChildIdSelf = 0;
    private const uint GaRoot = 2;
    private readonly WinEventDelegate _callback;
    // The hook is called on the thread that installed it, so one buffer is
    // enough; dragging a window fires these events at pointer rate.
    private readonly StringBuilder _classNameBuffer = new(128);
    private nint _hook;
    private bool _disposed;

    /// <summary>Raised when the taskbar window is recreated.</summary>
    public event EventHandler? TaskbarCreated;

    /// <summary>
    /// Raised when something inside the taskbar appears, disappears or moves -
    /// a tray icon going into the hidden icons, a taskbar button appearing, the
    /// taskbar being resized. Cheap enough to replace the UIA listener, which
    /// made a cross-process property call for every change.
    /// </summary>
    public event EventHandler? LayoutChanged;

    public TaskbarWindowMonitor()
    {
        _callback = OnWinEvent;
        _hook = SetWinEventHook(
            EventObjectCreate,
            EventObjectLocationChange,
            0,
            _callback,
            0,
            0,
            WineventOutOfContext | WineventSkipOwnProcess);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_hook != 0)
        {
            UnhookWinEvent(_hook);
            _hook = 0;
        }
    }

    private void OnWinEvent(
        nint hook,
        uint eventType,
        nint hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (_disposed || hwnd == 0 || objectId != ObjIdWindow || childId != ChildIdSelf)
            return;

        if (eventType is not (EventObjectCreate or EventObjectShow or EventObjectHide
            or EventObjectLocationChange))
            return;

        // Only window classes are interesting; resolving the root and its class
        // name are cheap local calls, unlike a UIA property read.
        var root = GetAncestor(hwnd, GaRoot);
        if (root == 0)
            return;

        _classNameBuffer.Clear();
        if (GetClassName(root, _classNameBuffer, _classNameBuffer.Capacity) <= 0 ||
            !TaskbarDetector.IsTaskbarClassName(_classNameBuffer.ToString()))
            return;

        if (eventType == EventObjectCreate && root == hwnd)
            TaskbarCreated?.Invoke(this, EventArgs.Empty);
        else
            LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);

    private delegate void WinEventDelegate(
        nint hook,
        uint eventType,
        nint hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint eventModule,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(
        nint hwnd,
        StringBuilder className,
        int maxCount);
}
