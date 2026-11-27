using System.Text;
using System.Windows;
using System.Windows.Automation;

namespace TaskbarMusicControls.Services;

internal sealed class TaskbarLayoutDetector
{
    private const int MaxDepth = 10;
    private const int MaxNodes = 500;

    /// <summary>
    /// How much room left of the icon group counts as "centred icons". Left
    /// aligned icons start a few pixels from the taskbar's left edge.
    /// </summary>
    private const double CenteredGapThreshold = 24;

    /// <summary>
    /// <paramref name="AvailableBounds"/> is the free space right of the task
    /// list; <paramref name="LeftBlockBounds"/> is the one left of it, which only
    /// exists when the taskbar icons are centred. Both are empty when there is no
    /// room. <paramref name="Centered"/> is true when the icon group does not
    /// start at the taskbar's left edge, i.e. when there are two blocks.
    /// </summary>
    internal sealed record TaskbarLayout(
        Rect TaskbarBounds,
        Rect AvailableBounds,
        Rect LeftBlockBounds,
        bool Centered);

    public TaskbarLayout? Detect(IntPtr taskbarHandle)
    {
        try
        {
            var cache = new CacheRequest
            {
                TreeScope = TreeScope.Element
            };
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
        if (state.TaskbarBounds.IsEmpty ||
            state.TaskbarBounds.Width <= 0 ||
            state.TaskbarBounds.Height <= 0)
        {
            TaskbarLayoutDiagnostics.Write("taskbar bounds are missing or invalid");
            return null;
        }
        if (state.AppButtonsBounds.Width <= 0)
        {
            TaskbarLayoutDiagnostics.Write("taskbar application buttons were not found");
            return null;
        }
        if (state.SystemTrayBounds.Width <= 0)
        {
            TaskbarLayoutDiagnostics.Write("system tray was not found");
            return null;
        }

        // The icon group is every direct child of the taskbar frame: the Start
        // button (class ToggleButton), search and task view included. The task
        // list buttons alone start after them, so measuring from those would let
        // the widget cover the Start button.
        var group = state.ContentBounds.Width > 0
            ? state.ContentBounds
            : state.AppButtonsBounds;

        var trayLeft = state.SystemTrayBounds.Left;
        if (trayLeft <= group.Right)
        {
            TaskbarLayoutDiagnostics.Write(
                $"available area is invalid: groupRight={group.Right:F0} trayLeft={trayLeft:F0}");
            return null;
        }

        var available = new Rect(
            group.Right,
            state.TaskbarBounds.Top,
            trayLeft - group.Right,
            state.TaskbarBounds.Height);

        // Icons pinned to the left leave no gap; with centred icons the group
        // starts well inside the taskbar and there is a second block before it.
        var leftGap = group.Left - state.TaskbarBounds.Left;
        var centered = leftGap >= CenteredGapThreshold;
        var leftBlock = leftGap > 0
            ? new Rect(
                state.TaskbarBounds.Left,
                state.TaskbarBounds.Top,
                leftGap,
                state.TaskbarBounds.Height)
            : Rect.Empty;
        TaskbarLayoutDiagnostics.Write(
            $"blocks: left={leftBlock} right={available} " +
            $"group={group} centered={centered}");
        if (state.Dump is { Length: > 0 })
            TaskbarLayoutDiagnostics.Write("uia dump:\n" + state.Dump);

        return new TaskbarLayout(state.TaskbarBounds, available, leftBlock, centered);
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
            var className = GetCached<string>(
                element,
                AutomationElement.ClassNameProperty) ?? string.Empty;
            var bounds = GetCached<Rect>(
                element,
                AutomationElement.BoundingRectangleProperty);

            if (className.StartsWith(
                    "Taskbar.TaskListButton",
                    StringComparison.Ordinal))
                state.AppButtonsBounds = Union(state.AppButtonsBounds, bounds);
            else if (className.StartsWith("SystemTray.", StringComparison.Ordinal) ||
                     className == "TrayNotifyWnd")
                state.SystemTrayBounds = Union(state.SystemTrayBounds, bounds);

            state.Dump?.AppendLine(
                $"{new string(' ', depth * 2)}{className} {bounds}");

            if (depth == MaxDepth || state.NodeCount >= MaxNodes)
                return;

            // The frame's direct children are the icon group - the Start, search
            // and task view buttons as well as the task list buttons - while the
            // system tray sits outside it.
            var isFrame = className.StartsWith(
                "Taskbar.TaskbarFrame",
                StringComparison.Ordinal);

            var walker = TreeWalker.ControlViewWalker;
            var child = walker.GetFirstChild(element, cache);
            while (child is not null && state.NodeCount < MaxNodes)
            {
                var next = walker.GetNextSibling(child, cache);
                if (isFrame)
                {
                    state.ContentBounds = Union(
                        state.ContentBounds,
                        GetCached<Rect>(
                            child,
                            AutomationElement.BoundingRectangleProperty));
                }

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
        public Rect AppButtonsBounds { get; set; }

        /// <summary>Union of the taskbar frame's direct children, Start included.</summary>
        public Rect ContentBounds { get; set; }
        public Rect SystemTrayBounds { get; set; }
        public int NodeCount { get; set; }

        /// <summary>
        /// Set when TBMC_DUMP_UIA=1 is in the environment: every node with its
        /// class name and bounds is written to the layout log, which is how the
        /// classes of the Start, search and task view buttons are found out.
        /// </summary>
        public StringBuilder? Dump { get; } =
            Environment.GetEnvironmentVariable("TBMC_DUMP_UIA") == "1"
                ? new StringBuilder()
                : null;
    }
}
