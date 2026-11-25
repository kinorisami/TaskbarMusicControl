using System.Windows.Automation;

namespace TaskbarMusicControls.Services;

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
            _handler = (sender, _) =>
            {
                if (sender is AutomationElement element && IsOwnProcessElement(element))
                    return;
                LayoutChanged?.Invoke(this, EventArgs.Empty);
            };
            Automation.AddStructureChangedEventHandler(
                _root,
                TreeScope.Subtree,
                _handler);
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

    private static bool IsOwnProcessElement(AutomationElement element)
    {
        try
        {
            return element.Current.ProcessId == Environment.ProcessId;
        }
        catch (ElementNotAvailableException)
        {
            return true;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return true;
        }
    }
}
