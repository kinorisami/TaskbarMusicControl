using System.Windows;
using System.Windows.Automation;

namespace TaskbarMusicControls.Services;

internal sealed class TaskbarLayoutDetector
{
    private const int MaxDepth = 10;
    private const int MaxNodes = 500;

    internal sealed record TaskbarLayout(Rect TaskbarBounds, Rect AvailableBounds);

    public TaskbarLayout? Detect(IntPtr taskbarHandle)
    {
        try
        {
            var cache = new CacheRequest
            {
                TreeScope = TreeScope.Element
            };
            cache.Add(AutomationElement.AutomationIdProperty);
            cache.Add(AutomationElement.ClassNameProperty);
            cache.Add(AutomationElement.BoundingRectangleProperty);

            using (cache.Activate())
            {
                var root = AutomationElement.FromHandle(taskbarHandle)
                    .GetUpdatedCache(cache);
                var state = new ScanState(GetCached<Rect>(
                    root,
                    AutomationElement.BoundingRectangleProperty));
                ScanTree(root, state, 0, cache);
                return BuildLayout(state);
            }
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static TaskbarLayout? BuildLayout(ScanState state)
    {
        var appsRight = state.AppButtonsBounds.Width <= 0
            ? state.StartButtonBounds.Right
            : state.AppButtonsBounds.Right;
        var trayLeft = state.SystemTrayBounds.Width <= 0
            ? state.TaskbarBounds.Right
            : state.SystemTrayBounds.Left;

        if (state.TaskbarBounds.IsEmpty ||
            state.TaskbarBounds.Width <= 0 ||
            state.TaskbarBounds.Height <= 0 ||
            trayLeft <= appsRight)
            return null;

        var available = new Rect(
            appsRight,
            state.TaskbarBounds.Top,
            trayLeft - appsRight,
            state.TaskbarBounds.Height);

        return new TaskbarLayout(state.TaskbarBounds, available);
    }

    private static void ScanTree(
        AutomationElement element,
        ScanState state,
        int depth,
        CacheRequest cache)
    {
        if (depth > MaxDepth || state.NodeCount >= MaxNodes)
            return;

        try
        {
            state.NodeCount++;
            var automationId = GetCached<string>(
                element,
                AutomationElement.AutomationIdProperty);
            var className = GetCached<string>(
                element,
                AutomationElement.ClassNameProperty) ?? string.Empty;
            var bounds = GetCached<Rect>(
                element,
                AutomationElement.BoundingRectangleProperty);

            if (automationId == "StartButton")
                state.StartButtonBounds = bounds;
            else if (className.StartsWith(
                         "Taskbar.TaskListButton",
                         StringComparison.Ordinal))
                state.AppButtonsBounds = Union(state.AppButtonsBounds, bounds);
            else if (className.StartsWith("SystemTray.", StringComparison.Ordinal) ||
                     className == "TrayNotifyWnd")
                state.SystemTrayBounds = Union(state.SystemTrayBounds, bounds);

            if (depth == MaxDepth || state.NodeCount >= MaxNodes)
                return;

            var walker = TreeWalker.ControlViewWalker;
            var child = walker.GetFirstChild(element, cache);
            while (child is not null && state.NodeCount < MaxNodes)
            {
                var next = walker.GetNextSibling(child, cache);
                ScanTree(child, state, depth + 1, cache);
                child = next;
            }
        }
        catch (ElementNotAvailableException)
        {
            // Shell elements can disappear while the taskbar tree is being read.
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Ignore inaccessible UIA nodes and continue with other taskbar windows.
        }
    }

    private static T GetCached<T>(AutomationElement element, AutomationProperty property)
    {
        var value = element.GetCachedPropertyValue(property, true);
        return value is T typed ? typed : default!;
    }

    private static Rect Union(Rect current, Rect candidate)
    {
        if (candidate.IsEmpty || candidate.Width <= 0 || candidate.Height <= 0)
            return current;

        if (current.IsEmpty || current.Width <= 0 || current.Height <= 0)
            return candidate;

        current.Union(candidate);
        return current;
    }

    private sealed class ScanState(Rect taskbarBounds)
    {
        public Rect TaskbarBounds { get; } = taskbarBounds;
        public Rect StartButtonBounds { get; set; }
        public Rect AppButtonsBounds { get; set; }
        public Rect SystemTrayBounds { get; set; }
        public int NodeCount { get; set; }
    }
}
