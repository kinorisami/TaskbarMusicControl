using System.Windows;
using System.Windows.Threading;
using TaskbarMusicControls.Interop;
using TaskbarMusicControls.Overlay;

namespace TaskbarMusicControls.Services;

internal sealed class TaskbarMusicWidgetService
{
    private const int TaskbarSettleTicks = 4;
    private readonly TaskbarDetector _taskbarDetector = new();
    private readonly TaskbarLayoutDetector _layoutDetector = new();
    private readonly TaskbarLayoutMonitor _layoutMonitor = new();
    private readonly TaskbarWindowMonitor _taskbarWindowMonitor = new();
    private readonly MediaSessionService _mediaSessionService = new();
    private readonly LyricsService _lyricsService = new();
    private readonly DispatcherTimer _lyricsTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(120)
    };
    private readonly DispatcherTimer _timelineSyncTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };
    private readonly DispatcherTimer _layoutTimer = new()
    {
        Interval = TimeSpan.FromSeconds(15)
    };
    private readonly DispatcherTimer _layoutChangeTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(150)
    };
    private readonly DispatcherTimer _nativeHandleTimer = new()
    {
        Interval = TimeSpan.FromSeconds(2)
    };
    private readonly DispatcherTimer _taskbarRecoveryTimer = new()
    {
        Interval = TimeSpan.FromSeconds(2)
    };

    private MusicOverlayWindow? _overlay;
    private IntPtr _watchedTaskbar;
    private bool _layoutRefreshInProgress;
    private bool _layoutRefreshRequested;
    private MediaSessionState _latestMediaState = MediaSessionState.Empty;
    private LyricsDocument? _lyrics;
    private CancellationTokenSource? _lyricsLookupCancellation;
    private string _lyricsTrackKey = "";
    private bool _lyricsMode;
    private WidgetDisplayMode _displayMode = WidgetDisplayMode.FullDetails;
    private LyricsSourcePreference _lyricsSourcePreference = LyricsSourcePreference.Automatic;
    private TimeSpan _lyricsOffset;
    private IReadOnlyList<TaskbarOption> _taskbarOptions = Array.Empty<TaskbarOption>();
    private string _selectedTaskbarDeviceId = "";
    private IntPtr _positionedTaskbar;
    private Rect _positionedTaskbarBounds;
    private Rect _positionedAvailableBounds;
    private bool _forceReposition;
    private bool _taskbarTemporarilyMissing;
    private int _taskbarSettleTicks;

    private sealed record TaskbarLayoutEntry(
        TaskbarInfo Taskbar,
        TaskbarLayoutDetector.TaskbarLayout Layout);

    public TaskbarMusicWidgetService()
    {
        _layoutTimer.Tick += OnLayoutTimerTick;
        _layoutChangeTimer.Tick += OnLayoutChangeTimerTick;
        _nativeHandleTimer.Tick += OnNativeHandleTimerTick;
        _taskbarRecoveryTimer.Tick += OnTaskbarRecoveryTimerTick;
        _lyricsTimer.Tick += OnLyricsTimerTick;
        _timelineSyncTimer.Tick += OnTimelineSyncTimerTick;
        _layoutMonitor.LayoutChanged += OnTaskbarLayoutChanged;
        _taskbarWindowMonitor.TaskbarCreated += OnTaskbarCreated;
        _mediaSessionService.StateChanged += OnMediaSessionStateChanged;
    }

    public async Task StartAsync()
    {
        _displayMode = WidgetDisplayModeStore.Load();
        _lyricsSourcePreference = LyricsSourceStore.Load();
        _selectedTaskbarDeviceId = TaskbarSelectionStore.Load() ?? "";
        _layoutTimer.Start();
        await _mediaSessionService.InitializeAsync();

        var layouts = await Task.Run(DetectTaskbarLayouts);
        _taskbarOptions = layouts.Select(entry => new TaskbarOption(
            entry.Taskbar.DeviceId,
            entry.Taskbar.DisplayName,
            entry.Taskbar.IsPrimary)).ToArray();

        foreach (var entry in OrderForSelection(layouts))
        {
            if (!ShowOverlay(entry.Taskbar.Handle, entry.Layout))
                continue;

            _selectedTaskbarDeviceId = entry.Taskbar.DeviceId;
            _overlay?.SetTaskbarSelection(entry.Taskbar.DeviceId);
            WatchTaskbar(entry.Taskbar.Handle);
            _nativeHandleTimer.Start();
            return;
        }
    }

    private bool ShowOverlay(
        IntPtr taskbarHandle,
        TaskbarLayoutDetector.TaskbarLayout layout)
    {
        var overlay = new MusicOverlayWindow();
        overlay.SetDisplayMode(_displayMode);
        overlay.SetTaskbarOptions(_taskbarOptions, _selectedTaskbarDeviceId);
        if (!overlay.PositionInAvailableArea(
                taskbarHandle,
                layout.TaskbarBounds,
                layout.AvailableBounds))
        {
            overlay.Close();
            return false;
        }

        _overlay = overlay;
        overlay.PreviousRequested += (_, _) =>
            _ = _mediaSessionService.SkipPreviousAsync();
        overlay.ToggleRequested += (_, _) =>
            _ = _mediaSessionService.TogglePlaybackAsync();
        overlay.NextRequested += (_, _) =>
            _ = _mediaSessionService.SkipNextAsync();
        overlay.LyricsModeChanged += OnLyricsModeChanged;
        overlay.LyricsSourceChanged += OnLyricsSourceChanged;
        overlay.LyricsOffsetChanged += OnLyricsOffsetChanged;
        overlay.DisplayModeChanged += OnDisplayModeChanged;
        overlay.TaskbarSelectionChanged += OnTaskbarSelectionChanged;
        overlay.Closed += OnOverlayClosed;

        var state = _mediaSessionService.CurrentState;
        _latestMediaState = state;
        overlay.SetLyricsSource(_lyricsSourcePreference);
        overlay.SetLyricsMode(_lyricsMode);
        overlay.UpdateMedia(state.Title, state.Artist, state.ArtworkBytes);
        ApplyMediaState(overlay, state);
        overlay.Show();
        overlay.PositionInAvailableArea(
            taskbarHandle,
            layout.TaskbarBounds,
            layout.AvailableBounds);
        overlay.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(overlay.RaiseToTop));

        _positionedTaskbar = taskbarHandle;
        _positionedTaskbarBounds = layout.TaskbarBounds;
        _positionedAvailableBounds = layout.AvailableBounds;

        Application.Current.MainWindow = overlay;
        return true;
    }

    private void OnDisplayModeChanged(object? sender, WidgetDisplayMode mode)
    {
        _displayMode = mode;
        WidgetDisplayModeStore.Save(mode);

        if (_overlay is { } overlay &&
            _positionedTaskbar != 0 &&
            !_positionedTaskbarBounds.IsEmpty &&
            !_positionedAvailableBounds.IsEmpty)
        {
            // Apply the new width/layout right away with the last known taskbar
            // layout instead of waiting for the debounced UIA re-scan.
            if (overlay.PositionInAvailableArea(
                    _positionedTaskbar,
                    _positionedTaskbarBounds,
                    _positionedAvailableBounds))
                return;
        }

        _forceReposition = true;
        ScheduleLayoutRefresh();
    }

    private void OnTaskbarSelectionChanged(object? sender, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) ||
            string.Equals(deviceId, _selectedTaskbarDeviceId, StringComparison.Ordinal))
            return;

        _selectedTaskbarDeviceId = deviceId;
        TaskbarSelectionStore.Save(deviceId);
        ScheduleLayoutRefresh();
    }

    private void OnLyricsSourceChanged(object? sender, LyricsSourcePreference preference)
    {
        if (_lyricsSourcePreference == preference)
            return;

        _lyricsSourcePreference = preference;
        LyricsSourceStore.Save(preference);
        if (_overlay is not null && _latestMediaState.Title != "No media playing")
            StartLyricsLookup(_latestMediaState);
    }

    private void OnLyricsOffsetChanged(object? sender, TimeSpan offset)
    {
        _lyricsOffset = offset;
        LyricsOffsetStore.Save(_lyricsTrackKey, offset);
    }

    private void OnMediaSessionStateChanged(MediaSessionState state)
    {
        var overlay = _overlay;
        if (overlay is null)
            return;

        _ = overlay.Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_overlay, overlay))
                return;
            _latestMediaState = state;
            ApplyMediaState(overlay, state);
        });
    }

    private void ApplyMediaState(MusicOverlayWindow overlay, MediaSessionState state)
    {
        var trackKey = $"{state.Artist}\u001f{state.Title}\u001f{state.Album}";
        if (!string.Equals(trackKey, _lyricsTrackKey, StringComparison.Ordinal))
        {
            _lyricsTrackKey = trackKey;
            _lyrics = null;
            _lyricsOffset = LyricsOffsetStore.Load(trackKey);
            overlay.SetLyricsOffset(_lyricsOffset);
            overlay.UpdateMedia(state.Title, state.Artist, state.ArtworkBytes);
            StartLyricsLookup(state);
        }
        else if (!ReferenceEquals(_lastArtwork, state.ArtworkBytes))
        {
            overlay.UpdateMedia(state.Title, state.Artist, state.ArtworkBytes);
        }
        _lastArtwork = state.ArtworkBytes;
        overlay.UpdatePlaybackState(state.IsPlaying, state.CanPlayPause,
            state.CanGoPrevious, state.CanGoNext);
        overlay.UpdateLyrics(_lyrics, _mediaSessionService.CurrentPosition);
        UpdateTimers(state);
    }

    private void StartLyricsLookup(MediaSessionState state)
    {
        _lyricsLookupCancellation?.Cancel();
        _lyricsLookupCancellation?.Dispose();
        _lyricsLookupCancellation = new CancellationTokenSource();
        _lyrics = null;
        if (_overlay is { } overlay)
            overlay.UpdateLyrics(null, _mediaSessionService.CurrentPosition);
        _ = FindLyricsAsync(_lyricsTrackKey, state, _lyricsSourcePreference,
            _lyricsLookupCancellation.Token);
    }

    private byte[]? _lastArtwork;

    private async Task FindLyricsAsync(
        string trackKey,
        MediaSessionState state,
        LyricsSourcePreference sourcePreference,
        CancellationToken token)
    {
        try
        {
            var result = await _lyricsService.FindAsync(
                new LyricsRequest(state.Title, state.Artist, state.Album, state.Duration),
                sourcePreference, token);
            if (token.IsCancellationRequested) return;
            var dispatcher = Application.Current.Dispatcher;
            await dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || trackKey != _lyricsTrackKey || _overlay is not { } overlay)
                    return;
                _lyrics = result;
                overlay.UpdateLyrics(_lyrics, _mediaSessionService.CurrentPosition);
                UpdateTimers(_latestMediaState);
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { /* A failed lyric lookup leaves the metadata visible. */ }
    }

    private void OnLyricsModeChanged(object? sender, bool enabled)
    {
        _lyricsMode = enabled;
        if (enabled)
        {
            _lyricsTimer.Stop();
            _ = RefreshPositionWhenLyricsOpenedAsync();
        }
        else
        {
            _lyricsTimer.Stop();
        }
        if (!enabled && _overlay is { } overlay)
            overlay.UpdateLyrics(_lyrics, _mediaSessionService.CurrentPosition);
        if (!enabled)
            _timelineSyncTimer.Stop();
    }

    private async Task RefreshPositionWhenLyricsOpenedAsync()
    {
        await _mediaSessionService.RefreshTimelineNowAsync();
        var state = _mediaSessionService.CurrentState;
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _latestMediaState = state;
            if (_overlay is { } overlay)
                overlay.UpdateLyrics(_lyrics, _mediaSessionService.CurrentPosition);
            UpdateTimers(state);
        });
    }

    private void OnLyricsTimerTick(object? sender, EventArgs e)
    {
        if (_overlay is { } overlay)
            overlay.UpdateLyrics(_lyrics, _mediaSessionService.CurrentPosition);
    }

    private void OnTimelineSyncTimerTick(object? sender, EventArgs e)
        => _ = _mediaSessionService.RefreshTimelineNowAsync();

    private void UpdateTimers(MediaSessionState state)
    {
        var shouldRun = _lyricsMode && _lyrics?.IsTimed == true && state.IsPlaying;
        if (shouldRun)
        {
            _lyricsTimer.Start();
            _timelineSyncTimer.Start();
        }
        else
        {
            _lyricsTimer.Stop();
            _timelineSyncTimer.Stop();
        }
    }

    private void OnLayoutTimerTick(object? sender, EventArgs e)
        => _ = RefreshTaskbarLayoutAsync();

    private void OnTaskbarLayoutChanged(object? sender, EventArgs e)
        => ScheduleLayoutRefresh();

    private void OnTaskbarCreated(object? sender, EventArgs e)
        => ScheduleLayoutRefresh();

    private void ScheduleLayoutRefresh()
    {
        var dispatcher = Application.Current.Dispatcher;
        _ = dispatcher.BeginInvoke(() =>
        {
            _layoutChangeTimer.Stop();
            _layoutChangeTimer.Start();
        });
    }

    private void OnLayoutChangeTimerTick(object? sender, EventArgs e)
    {
        _layoutChangeTimer.Stop();
        _ = RefreshTaskbarLayoutAsync();
    }

    private void OnNativeHandleTimerTick(object? sender, EventArgs e)
    {
        if (_overlay is null || !_overlay.HasValidNativeHandle)
            _ = RefreshTaskbarLayoutAsync();
    }

    private void OnTaskbarRecoveryTimerTick(object? sender, EventArgs e)
    {
        // The probe only runs while the taskbar is known to be temporarily
        // missing from the window list, so the normal case costs nothing.
        try
        {
            if (!_taskbarDetector.HasTaskbarWindow())
                return;
        }
        catch (Exception)
        {
            return;
        }

        if (_taskbarSettleTicks <= 0)
        {
            // The taskbar is enumerable again. Refresh right away and keep
            // refreshing for a short settle window: the shell often commits the
            // new taskbar layout (a tray icon moved into the hidden icons, a
            // new taskbar button) a moment after the flyout is closed, and a
            // single refresh would still see the old layout.
            _taskbarSettleTicks = TaskbarSettleTicks;
            TaskbarLayoutDiagnostics.Write(
                "taskbar is enumerable again: scheduling layout refreshes");
        }
        else
        {
            _taskbarSettleTicks--;
            if (_taskbarSettleTicks == 0)
            {
                _taskbarRecoveryTimer.Stop();
                TaskbarLayoutDiagnostics.Write("taskbar settle window finished");
                return;
            }
        }

        ScheduleLayoutRefresh();
    }

    private async Task RefreshTaskbarLayoutAsync()
    {
        if (_layoutRefreshInProgress)
        {
            _layoutRefreshRequested = true;
            return;
        }

        _layoutRefreshInProgress = true;
        try
        {
            do
            {
                _layoutRefreshRequested = false;
                var layouts = await Task.Run(DetectTaskbarLayouts);
                _taskbarOptions = layouts.Select(entry => new TaskbarOption(
                    entry.Taskbar.DeviceId,
                    entry.Taskbar.DisplayName,
                    entry.Taskbar.IsPrimary)).ToArray();
                _overlay?.SetTaskbarOptions(_taskbarOptions, _selectedTaskbarDeviceId);
                var foundLayout = false;

                foreach (var entry in OrderForSelection(layouts))
                {
                    foundLayout = true;
                    var taskbar = entry.Taskbar.Handle;
                    var layout = entry.Layout;
                    WatchTaskbar(taskbar);

                    if (_taskbarTemporarilyMissing)
                    {
                        // The taskbar is enumerable again after a shell flyout was
                        // closed. The layout comparison below decides whether the
                        // overlay has to be moved; the probe keeps refreshing for a
                        // short settle window because the shell can commit the new
                        // taskbar layout a moment later.
                        _taskbarTemporarilyMissing = false;
                        TaskbarLayoutDiagnostics.Write(
                            "taskbar is enumerable again: layout refreshed");
                    }

                    if (_overlay is null)
                    {
                        if (ShowOverlay(taskbar, layout))
                        {
                            _selectedTaskbarDeviceId = entry.Taskbar.DeviceId;
                            _overlay?.SetTaskbarSelection(entry.Taskbar.DeviceId);
                            _nativeHandleTimer.Start();
                        }
                        break;
                    }

                    if (!_overlay.HasValidNativeHandle)
                    {
                        RecreateOverlay(taskbar, layout);
                        break;
                    }

                    var forceReposition = _forceReposition;
                    _forceReposition = false;
                    var samePlacement = !forceReposition &&
                        _positionedTaskbar == taskbar &&
                        RectsNearEqual(layout.TaskbarBounds, _positionedTaskbarBounds) &&
                        RectsNearEqual(layout.AvailableBounds, _positionedAvailableBounds);

                    if (!samePlacement)
                    {
                        TaskbarLayoutDiagnostics.Write(
                            $"reposition overlay: taskbar={taskbar} " +
                            $"available={layout.AvailableBounds}");
                        if (!_overlay.PositionInAvailableArea(
                                taskbar,
                                layout.TaskbarBounds,
                                layout.AvailableBounds))
                        {
                            TaskbarLayoutDiagnostics.Write(
                                "hide overlay: failed to position in available area");
                            _overlay.Hide();
                            break;
                        }

                        _positionedTaskbar = taskbar;
                        _positionedTaskbarBounds = layout.TaskbarBounds;
                        _positionedAvailableBounds = layout.AvailableBounds;

                        if (!_overlay.IsVisible)
                        {
                            TaskbarLayoutDiagnostics.Write("show overlay after reposition");
                            _overlay.Show();
                        }
                    }
                    else if (!_overlay.IsVisible)
                    {
                        _overlay.Show();
                    }

                    break;
                }

                if (!foundLayout && _overlay is { HasValidNativeHandle: true })
                {
                    if (IsKnownTaskbarAlive())
                    {
                        // Explorer stops exposing the taskbar as a top-level
                        // window while a shell flyout (the Start menu, the
                        // notification center, ...) is open. The taskbar and the
                        // widget are still alive, so the widget stays on screen
                        // and a probe watches for the taskbar coming back.
                        if (!_taskbarTemporarilyMissing)
                        {
                            TaskbarLayoutDiagnostics.Write(
                                "keep overlay: taskbar is temporarily not enumerable");
                        }
                        _taskbarTemporarilyMissing = true;
                        _taskbarSettleTicks = 0;
                        _taskbarRecoveryTimer.Start();
                    }
                    else if (!HasTaskbarWindow())
                    {
                        TaskbarLayoutDiagnostics.Write(
                            "hide overlay: no taskbar window is available");
                        _overlay.Hide();
                    }
                }
            }
            while (_layoutRefreshRequested);
        }
        finally
        {
            _layoutRefreshInProgress = false;
        }
    }

    private bool HasTaskbarWindow()
    {
        // While the Start menu (or another shell flyout) is open, Explorer stops
        // exposing the taskbar as a top-level window, so the enumeration below
        // comes back empty even though the taskbar is still there and the widget
        // is still attached to it. Keep the widget visible whenever the taskbar
        // window we already know about is still alive.
        if (IsKnownTaskbarAlive())
            return true;

        try
        {
            return _taskbarDetector.DetectTaskbars().Count > 0;
        }
        catch (Exception)
        {
            // If enumeration fails, keep the overlay where it is instead of hiding it.
            return true;
        }
    }

    private bool IsKnownTaskbarAlive()
    {
        foreach (var handle in new[] { _positionedTaskbar, _watchedTaskbar })
        {
            if (handle == 0)
                continue;
            if (NativeMethods.IsWindow(handle))
                return true;
        }

        return false;
    }

    private static bool RectsNearEqual(Rect left, Rect right)
    {
        const double tolerance = 10;
        return Math.Abs(left.X - right.X) <= tolerance &&
               Math.Abs(left.Y - right.Y) <= tolerance &&
               Math.Abs(left.Width - right.Width) <= tolerance &&
               Math.Abs(left.Height - right.Height) <= tolerance;
    }

    private void RecreateOverlay(
        IntPtr taskbarHandle,
        TaskbarLayoutDetector.TaskbarLayout layout)
    {
        var previous = _overlay;
        if (previous is not null)
        {
            previous.Closed -= OnOverlayClosed;
            try
            {
                previous.Close();
            }
            catch (InvalidOperationException)
            {
                // Explorer may already have destroyed the child HWND.
            }
        }

        _overlay = null;
        ShowOverlay(taskbarHandle, layout);
    }

    private TaskbarLayoutEntry[] DetectTaskbarLayouts()
    {
        var taskbars = _taskbarDetector.DetectTaskbars()
            .OrderBy(taskbar => taskbar.IsPrimary ? 0 : 1)
            .ThenBy(taskbar => taskbar.Bounds.X)
            .ThenBy(taskbar => taskbar.Bounds.Y)
            .ToArray();

        var layouts = new List<TaskbarLayoutEntry>();
        foreach (var taskbar in taskbars)
        {
            var layout = _layoutDetector.Detect(taskbar.Handle);
            if (layout is not null && !layout.AvailableBounds.IsEmpty)
                layouts.Add(new TaskbarLayoutEntry(taskbar, layout));
        }

        return layouts.ToArray();
    }

    private TaskbarLayoutEntry[] OrderForSelection(IReadOnlyList<TaskbarLayoutEntry> layouts)
    {
        if (layouts.Count == 0)
            return Array.Empty<TaskbarLayoutEntry>();

        var selected = (_selectedTaskbarDeviceId.Length > 0
                ? layouts.FirstOrDefault(entry =>
                    string.Equals(entry.Taskbar.DeviceId, _selectedTaskbarDeviceId,
                        StringComparison.Ordinal))
                : null)
            ?? layouts.FirstOrDefault(entry => entry.Taskbar.IsPrimary)
            ?? layouts[0];
        var ordered = new List<TaskbarLayoutEntry> { selected };
        foreach (var entry in layouts)
        {
            if (!ReferenceEquals(entry, selected))
                ordered.Add(entry);
        }
        return ordered.ToArray();
    }

    private void OnOverlayClosed(object? sender, EventArgs e)
    {
        _layoutTimer.Stop();
        _layoutChangeTimer.Stop();
        _nativeHandleTimer.Stop();
        _taskbarRecoveryTimer.Stop();
        _lyricsTimer.Stop();
        _timelineSyncTimer.Stop();
        _lyricsLookupCancellation?.Cancel();
        _lyricsLookupCancellation?.Dispose();
        Application.Current.Shutdown();
    }

    private void WatchTaskbar(IntPtr taskbarHandle)
    {
        if (_watchedTaskbar == taskbarHandle)
            return;

        _watchedTaskbar = taskbarHandle;
        _layoutMonitor.Watch(taskbarHandle);
    }
}
