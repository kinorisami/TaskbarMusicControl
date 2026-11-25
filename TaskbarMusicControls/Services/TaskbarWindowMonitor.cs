using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarMusicControls.Services;

internal sealed class TaskbarWindowMonitor : IDisposable
{
    private const uint EventObjectCreate = 0x8000;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;
    private const int ObjIdWindow = 0;
    private const int ChildIdSelf = 0;
    private readonly WinEventDelegate _callback;
    private nint _hook;
    private bool _disposed;

    public event EventHandler? TaskbarCreated;

    public TaskbarWindowMonitor()
    {
        _callback = OnWinEvent;
        _hook = SetWinEventHook(
            EventObjectCreate,
            EventObjectCreate,
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
        if (_disposed || eventType != EventObjectCreate || hwnd == 0 ||
            objectId != ObjIdWindow || childId != ChildIdSelf)
            return;

        var className = new StringBuilder(128);
        if (GetClassName(hwnd, className, className.Capacity) > 0 &&
            TaskbarDetector.IsTaskbarClassName(className.ToString()))
            TaskbarCreated?.Invoke(this, EventArgs.Empty);
    }

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
