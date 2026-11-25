using System.Windows;
using System.Windows.Threading;
using System.Diagnostics;
using TaskbarMusicControls.Overlay;

namespace TaskbarMusicControls.Services;

internal sealed class TaskbarMusicWidgetService
{
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
    private readonly DispatcherTimer _layoutTimer = new()
    {
        Interval = TimeSpan.FromSeconds(15)
    };
    private readonly DispatcherTimer _layoutChangeTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(120)
    };
    private readonly DispatcherTimer _nativeHandleTimer = new()
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

    public TaskbarMusicWidgetService()
    {
        _layoutTimer.Tick += OnLayoutTimerTick;
        _layoutChangeTimer.Tick += OnLayoutChangeTimerTick;
        _nativeHandleTimer.Tick += OnNativeHandleTimerTick;
        _lyricsTimer.Tick += OnLyricsTimerTick;
        _layoutMonitor.LayoutChanged += OnTaskbarLayoutChanged;
        _taskbarWindowMonitor.TaskbarCreated += OnTaskbarCreated;
        _mediaSessionService.StateChanged += OnMediaSessionStateChanged;
    }

    public async Task StartAsync()
    {
        _layoutTimer.Start();
        await _mediaSessionService.InitializeAsync();

        var layouts = await Task.Run(DetectTaskbarLayouts);
        foreach (var (taskbar, layout) in layouts)
        {
            if (!ShowOverlay(taskbar, layout))
                continue;

            WatchTaskbar(taskbar);
            _nativeHandleTimer.Start();
            return;
        }
    }

    private bool ShowOverlay(
        IntPtr taskbarHandle,
        TaskbarLayoutDetector.TaskbarLayout layout)
    {
        var overlay = new MusicOverlayWindow();
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
        overlay.Closed += OnOverlayClosed;

        var state = _mediaSessionService.CurrentState;
        _latestMediaState = state;
        overlay.SetLyricsMode(_lyricsMode);
        overlay.UpdateMedia(state.Title, state.Artist, state.ArtworkBytes);
        ApplyMediaState(overlay, state);
        overlay.Show();
        overlay.PositionInAvailableArea(
            taskbarHandle,
            layout.TaskbarBounds,
            layout.AvailableBounds);

        Application.Current.MainWindow = overlay;
        return true;
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
            _lyricsLookupCancellation?.Cancel();
            _lyricsLookupCancellation?.Dispose();
            _lyricsLookupCancellation = new CancellationTokenSource();
            _lyrics = null;
            overlay.UpdateMedia(state.Title, state.Artist, state.ArtworkBytes);
            _ = FindLyricsAsync(trackKey, state, _lyricsLookupCancellation.Token);
        }
        else if (!ReferenceEquals(_lastArtwork, state.ArtworkBytes))
        {
            overlay.UpdateMedia(state.Title, state.Artist, state.ArtworkBytes);
        }
        _lastArtwork = state.ArtworkBytes;
        overlay.UpdatePlaybackState(state.IsPlaying, state.CanPlayPause,
            state.CanGoPrevious, state.CanGoNext);
        overlay.UpdateLyrics(_lyrics, GetCurrentPosition(state));
    }

    private byte[]? _lastArtwork;

    private async Task FindLyricsAsync(string trackKey, MediaSessionState state, CancellationToken token)
    {
        try
        {
            var result = await _lyricsService.FindAsync(
                new LyricsRequest(state.Title, state.Artist, state.Album, state.Duration), token);
            if (token.IsCancellationRequested) return;
            var dispatcher = Application.Current.Dispatcher;
            await dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || trackKey != _lyricsTrackKey || _overlay is not { } overlay)
                    return;
                _lyrics = result;
                if (_lyricsMode && _lyrics?.IsTimed == true) _lyricsTimer.Start();
                overlay.UpdateLyrics(_lyrics, GetCurrentPosition(_latestMediaState));
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { /* A failed lyric lookup leaves the metadata visible. */ }
    }

    private static TimeSpan GetCurrentPosition(MediaSessionState state)
    {
        var position = state.Position;
        if (state.IsPlaying && state.PositionUpdatedAt != 0)
            position += Stopwatch.GetElapsedTime(state.PositionUpdatedAt);
        if (state.Duration > TimeSpan.Zero && position > state.Duration)
            position = state.Duration;
        return position < TimeSpan.Zero ? TimeSpan.Zero : position;
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
            overlay.UpdateLyrics(_lyrics, GetCurrentPosition(_latestMediaState));
    }

    private async Task RefreshPositionWhenLyricsOpenedAsync()
    {
        await _mediaSessionService.RefreshTimelineNowAsync();
        var state = _mediaSessionService.CurrentState;
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _latestMediaState = state;
            if (_overlay is { } overlay)
                overlay.UpdateLyrics(_lyrics, GetCurrentPosition(state));
            if (_lyricsMode && _lyrics?.IsTimed == true)
                _lyricsTimer.Start();
        });
    }

    private void OnLyricsTimerTick(object? sender, EventArgs e)
    {
        if (_overlay is { } overlay)
            overlay.UpdateLyrics(_lyrics, GetCurrentPosition(_latestMediaState));
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
                var foundLayout = false;

                foreach (var (taskbar, layout) in layouts)
                {
                    foundLayout = true;
                    WatchTaskbar(taskbar);

                    if (_overlay is null)
                    {
                        if (ShowOverlay(taskbar, layout))
                            _nativeHandleTimer.Start();
                        break;
                    }

                    if (!_overlay.HasValidNativeHandle)
                    {
                        RecreateOverlay(taskbar, layout);
                        break;
                    }

                    if (!_overlay.IsWithinAvailableArea(
                            taskbar,
                            layout.TaskbarBounds,
                            layout.AvailableBounds))
                    {
                        if (!_overlay.PositionInAvailableArea(
                                taskbar,
                                layout.TaskbarBounds,
                                layout.AvailableBounds))
                        {
                            _overlay.Hide();
                            break;
                        }

                        if (!_overlay.IsVisible)
                            _overlay.Show();
                    }

                    break;
                }

                if (!foundLayout && _overlay is { HasValidNativeHandle: true })
                    _overlay.Hide();
            }
            while (_layoutRefreshRequested);
        }
        finally
        {
            _layoutRefreshInProgress = false;
        }
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

    private (IntPtr Taskbar, TaskbarLayoutDetector.TaskbarLayout Layout)[]
        DetectTaskbarLayouts()
    {
        var layouts = new List<(IntPtr, TaskbarLayoutDetector.TaskbarLayout)>();
        foreach (var taskbar in _taskbarDetector.DetectTaskbars())
        {
            var layout = _layoutDetector.Detect(taskbar);
            if (layout is not null && !layout.AvailableBounds.IsEmpty)
                layouts.Add((taskbar, layout));
        }

        return layouts.ToArray();
    }

    private void OnOverlayClosed(object? sender, EventArgs e)
    {
        _layoutTimer.Stop();
        _layoutChangeTimer.Stop();
        _nativeHandleTimer.Stop();
        _lyricsTimer.Stop();
        _lyricsLookupCancellation?.Cancel();
        _lyricsLookupCancellation?.Dispose();
        _layoutMonitor.Dispose();
        _taskbarWindowMonitor.Dispose();
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
