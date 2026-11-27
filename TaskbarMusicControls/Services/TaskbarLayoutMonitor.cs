using System.Windows.Automation;

namespace TaskbarMusicControls.Services;

/// <summary>
/// Notices changes inside the taskbar that have no window of their own - a
/// taskbar button appearing, a tray icon moving into the hidden icons - by
/// listening to UIA structure changes on the taskbar subtree.
/// </summary>
/// <remarks>
/// The handler deliberately does not read any property from the event's element:
/// doing that made a cross-process call for every event, and the burst that
/// comes with opening a menu froze the UI thread for seconds. Debouncing is left
/// to the caller.
/// </remarks>
internal sealed class TaskbarLayoutMonitor : IDisposable
{
    private AutomationElement? _root;
    private StructureChangedEventHandler? _handler;

    public event EventHandler? LayoutChanged;

    public void Watch(IntPtr taskbarHandle)
    {
        StopWatching();

        try
        {
            _root = AutomationElement.FromHandle(taskbarHandle);
            _handler = (_, _) => LayoutChanged?.Invoke(this, EventArgs.Empty);
            Automation.AddStructureChangedEventHandler(_root, TreeScope.Subtree, _handler);
        }
        catch (ElementNotAvailableException)
        {
            StopWatching();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            StopWatching();
        }
    }

    public void Dispose() => StopWatching();

    private void StopWatching()
    {
        if (_root is not null && _handler is not null)
        {
            try
            {
                Automation.RemoveStructureChangedEventHandler(_root, _handler);
            }
            catch (ElementNotAvailableException)
            {
                // Explorer can recreate its taskbar while the handler is detached.
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // The UIA provider may already be gone.
            }
        }

        _root = null;
        _handler = null;
    }
}
