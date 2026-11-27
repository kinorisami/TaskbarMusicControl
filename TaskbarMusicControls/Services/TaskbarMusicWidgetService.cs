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
    private static readonly TimeSpan NormalPollInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// After the task list changes the shell re-centres the icon group, and that
    /// movement fires no event at all: the buttons are XAML elements without a
    /// window. The widget therefore polls quickly for a while after every change
    /// instead of waiting up to a full 15 second poll to notice the new width.
    /// </summary>
    private static readonly TimeSpan ProbePollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ProbeDuration = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many quiet scans end the probe. One scan costs about 45 ms of a pool
    /// thread and a UI Automation call into Explorer, so the probe stops once the
    /// layout has been seen unchanged a few times instead of running its full
    /// length every time.
    /// </summary>
    private const int ProbeStableScansToStop = 3;
    private int _probeStableScans;

    private readonly DispatcherTimer _layoutTimer = new()
    {
        Interval = NormalPollInterval
    };
    private DateTime _probeUntil = DateTime.MinValue;
    private readonly DispatcherTimer _layoutChangeTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(150)
    };
    private readonly DispatcherTimer _pauseHideTimer = new();

    private readonly DispatcherTimer _settleRefreshTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(400)
    };
    private int _settleRefreshesLeft;
    private Rect _settleLastAvailable;
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
    private LyricsSongState _songState = LyricsSongState.Empty;
    private bool _lyricsMode;
    private bool _showTranslation = true;
    private bool _startWithWindows;
    private bool _hideWhenIdle;
    private bool _manualHidden;
    private int _pauseHideSeconds;
    private bool _pausedHidden;
    private bool _showForced;
    private bool _widgetHidden;
    private string _playerId = "";
    private bool _exitRequested;
    private TrayIcon? _trayIcon;
    private WidgetBlock _block = WidgetBlock.Right;
    private WidgetAlignment _alignment = WidgetAlignment.Right;
    private bool _taskbarCentered;
    private WidgetDisplayMode _displayMode = WidgetDisplayMode.Full;
    private LyricsSourceSettings _lyricsSourceSettings = LyricsSourceSettings.Default;
    private TimeSpan _lyricsOffset;
    private IReadOnlyList<TaskbarOption> _taskbarOptions = Array.Empty<TaskbarOption>();
    private string _selectedTaskbarDeviceId = "";
    private IntPtr _positionedTaskbar;
    private Rect _positionedTaskbarBounds;
    private Rect _positionedAvailableBounds;
    private Rect _positionedLeftBlockBounds;
    private Rect _positionedBlockBounds;
    private bool _positionedCentered;
    private bool _forceReposition;
    private bool _taskbarTemporarilyMissing;
    private int _taskbarSettleTicks;
    private bool _attachmentFailureReported;

    private sealed record TaskbarLayoutEntry(
        TaskbarInfo Taskbar,
        TaskbarLayoutDetector.TaskbarLayout Layout);

    public TaskbarMusicWidgetService()
    {
        _layoutTimer.Tick += OnLayoutTimerTick;
        _layoutChangeTimer.Tick += OnLayoutChangeTimerTick;
        _settleRefreshTimer.Tick += OnSettleRefreshTimerTick;
        _pauseHideTimer.Tick += OnPauseHideTimerTick;
        _nativeHandleTimer.Tick += OnNativeHandleTimerTick;
        _taskbarRecoveryTimer.Tick += OnTaskbarRecoveryTimerTick;
        _lyricsTimer.Tick += OnLyricsTimerTick;
        _timelineSyncTimer.Tick += OnTimelineSyncTimerTick;
        _layoutMonitor.LayoutChanged += OnTaskbarLayoutChanged;
        _taskbarWindowMonitor.LayoutChanged += OnTaskbarLayoutChanged;
        _taskbarWindowMonitor.TaskbarCreated += OnTaskbarCreated;
        _mediaSessionService.StateChanged += OnMediaSessionStateChanged;
    }

    public async Task StartAsync()
    {
        var settings = WidgetSettingsStore.Load(out var needsWrite);
        _displayMode = settings.DisplayMode;
        _lyricsSourceSettings = settings.LyricsSources;
        _selectedTaskbarDeviceId = settings.TaskbarDeviceId;
        _lyricsMode = settings.LyricsMode;
        _showTranslation = settings.ShowTranslation;
        _startWithWindows = settings.StartWithWindows;
        _hideWhenIdle = settings.HideWhenIdle;
        _manualHidden = settings.ManualHidden;
        _pauseHideSeconds = settings.PauseHideSeconds;
        _playerId = settings.PlayerId;
        _block = settings.Block;
        _alignment = settings.Alignment;
        _taskbarCentered = settings.TaskbarCentered;

        // Only when the file was missing or a key was absent: the defaults of the
        // new keys are written once, not on every start.
        if (needsWrite)
            SaveSettings();

        // settings.json is the source of truth; the Run key follows it.
        if (_startWithWindows != AutostartStore.IsRegistered())
            AutostartStore.Set(_startWithWindows);
        _layoutTimer.Start();
        // Pin the saved player before the first session is resolved, so the
        // widget starts on it when it is running.
        _mediaSessionService.SelectPlayer(_playerId);
        _mediaSessionService.PlayersChanged += OnPlayersChanged;
        StartTrayIcon();
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
        // A layout refresh can run while StartAsync is still waiting for the
        // media session or the first taskbar scan. Creating a second window here
        // would leave an orphaned overlay behind the visible one.
        if (_overlay is not null)
            return true;

        var overlay = new MusicOverlayWindow();
        overlay.SetDisplayMode(_displayMode);
        overlay.SetTaskbarOptions(_taskbarOptions, _selectedTaskbarDeviceId);
        var position = overlay.PositionInAvailableArea(
            taskbarHandle,
            layout.TaskbarBounds,
            layout.AvailableBounds,
            layout.LeftBlockBounds,
            layout.Centered);
        if (position != TaskbarPositionResult.Positioned)
        {
            ReportPositionFailure(position, taskbarHandle);
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
        overlay.SeekRequested += (_, position) =>
            _ = _mediaSessionService.SeekAsync(position);
        overlay.PlayerActivateRequested += (_, _) =>
            _mediaSessionService.TogglePlayerWindow();
        overlay.BlockChanged += OnBlockChanged;
        overlay.AlignmentChanged += OnAlignmentChanged;
        overlay.TaskbarCenteredChanged += OnTaskbarCenteredChanged;
        overlay.LyricsModeChanged += OnLyricsModeChanged;
        overlay.LyricsSourceSettingsChanged += OnLyricsSourceSettingsChanged;
        overlay.LyricsLockChanged += OnLyricsLockChanged;
        overlay.LyricsRefetchRequested += (_, _) => RefetchLyrics();
        overlay.LyricsOffsetChanged += OnLyricsOffsetChanged;
        overlay.TranslationChanged += OnTranslationChanged;
        overlay.StartupChanged += OnStartupChanged;
        overlay.HideWhenIdleChanged += OnHideWhenIdleChanged;
        overlay.ManualHiddenChanged += OnManualHiddenChanged;
        overlay.PauseHideChanged += OnPauseHideChanged;
        overlay.PlayerSelectedChanged += OnPlayerSelectedChanged;
        overlay.DisplayModeChanged += OnDisplayModeChanged;
        overlay.TaskbarSelectionChanged += OnTaskbarSelectionChanged;
        overlay.Closed += OnOverlayClosed;

        var state = _mediaSessionService.CurrentState;
        _latestMediaState = state;
        overlay.SetLyricsSourceSettings(_lyricsSourceSettings);
        overlay.SetLyricsMode(_lyricsMode);
        overlay.SetShowTranslation(_showTranslation);
        overlay.SetStartWithWindows(_startWithWindows);
        overlay.SetHideWhenIdle(_hideWhenIdle);
        overlay.SetManualHidden(_manualHidden);
        overlay.SetPauseHideSeconds(_pauseHideSeconds);
        overlay.SetPlayerOptions(
            _mediaSessionService.Players, _mediaSessionService.ActivePlayerId, _playerId);
        overlay.SetBlock(_block);
        overlay.SetAlignment(_alignment);
        overlay.UpdateMedia(state.Title, state.Artist, state.ArtworkBytes);
        ApplyMediaState(overlay, state);
        overlay.Show();
        var finalPosition = overlay.PositionInAvailableArea(
            taskbarHandle,
            layout.TaskbarBounds,
            layout.AvailableBounds,
            layout.LeftBlockBounds,
            layout.Centered);
        if (finalPosition != TaskbarPositionResult.Positioned)
            ReportPositionFailure(finalPosition, taskbarHandle);
        overlay.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(overlay.RaiseToTop));

        _positionedTaskbar = taskbarHandle;
        _positionedTaskbarBounds = layout.TaskbarBounds;
        _positionedAvailableBounds = layout.AvailableBounds;
        _positionedLeftBlockBounds = layout.LeftBlockBounds;
        _positionedCentered = layout.Centered;
        _positionedBlockBounds = ChosenBlock(layout);
        RememberTaskbarCentered(layout.Centered);

        Application.Current.MainWindow = overlay;
        return true;
    }

    /// <summary>Writes every setting, so the file always holds the full state.</summary>
    private void SaveSettings() => WidgetSettingsStore.Save(new WidgetSettings(
        _displayMode, _selectedTaskbarDeviceId, _lyricsSourceSettings,
        _lyricsMode, _showTranslation, _startWithWindows, _hideWhenIdle,
        _manualHidden, _pauseHideSeconds, _playerId,
        _block, _alignment,
        _taskbarCentered));

    private void OnDisplayModeChanged(object? sender, WidgetDisplayMode mode)
    {
        _displayMode = mode;
        SaveSettings();

        if (_overlay is { } overlay &&
            _positionedTaskbar != 0 &&
            !_positionedTaskbarBounds.IsEmpty &&
            !_positionedAvailableBounds.IsEmpty)
        {
            // Apply the new width/layout right away with the last known taskbar
            // layout instead of waiting for the debounced UIA re-scan.
            var position = overlay.PositionInAvailableArea(
                _positionedTaskbar,
                _positionedTaskbarBounds,
                _positionedAvailableBounds,
                _positionedLeftBlockBounds,
                _positionedCentered);
            if (position == TaskbarPositionResult.Positioned)
                return;
            ReportPositionFailure(position, _positionedTaskbar);
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
        SaveSettings();
        ScheduleLayoutRefresh();
    }

    private void OnLyricsSourceSettingsChanged(object? sender, LyricsSourceSettings settings)
    {
        if (_lyricsSourceSettings == settings)
            return;

        _lyricsSourceSettings = settings;
        SaveSettings();

        // A song pinned to a source keeps it; the global setting only decides
        // how everything else is looked up.
        if (_songState.LockedSource is not null ||
            _overlay is null || _latestMediaState.Title == "No media playing")
            return;

        // A plain lookup, not a re-fetch: the new order is applied on top of the
        // caches, so a source that already has lyrics costs no network call.
        // Only the explicit "Refresh lyrics" command skips the caches.
        StartLyricsLookup(_latestMediaState);
    }

    /// <summary>
    /// Pins the song on screen to the source its lyrics came from, or lets it
    /// follow the global setting again.
    /// </summary>
    private void OnLyricsLockChanged(object? sender, bool locked)
    {
        var current = _lyrics?.Source ?? _songState.LastSource;
        var target = locked ? current : null;
        if (locked && target is null)
        {
            // Locking pins the source that is on screen; before the lyrics
            // arrive there is nothing to pin.
            _overlay?.SetLyricsLock(false, null);
            return;
        }

        if (_songState.LockedSource == target)
            return;

        var wasLocked = _songState.LockedSource is not null;
        _songState = _songState with { LockedSource = target };
        LyricsSongStateStore.Save(_lyricsTrackKey, _songState);

        if (_overlay is { } overlay)
        {
            overlay.SetLyricsLock(target is not null, current);
            overlay.SetLyricsStatus(current);
        }

        // Unlocking hands the song back to the global setting, which may pick a
        // different source; locking keeps exactly what is on screen.
        if (wasLocked && target is null &&
            _overlay is not null && _latestMediaState.Title != "No media playing")
            StartLyricsLookup(_latestMediaState);
    }

    /// <summary>
    /// Throws away what is cached for the current song and looks it up again -
    /// the way out of a wrong match that the cache would otherwise keep.
    /// </summary>
    private void RefetchLyrics()
    {
        if (_overlay is null || _latestMediaState.Title == "No media playing")
            return;

        StartLyricsLookup(_latestMediaState, refetch: true);
    }

    private void OnLyricsOffsetChanged(object? sender, TimeSpan offset)
    {
        _lyricsOffset = offset;
        _songState = _songState with { Offset = offset };
        LyricsSongStateStore.Save(_lyricsTrackKey, _songState);
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
        UpdatePauseHide(state);
        UpdateVisibility();
        overlay.SetPlayerOptions(
            _mediaSessionService.Players, _mediaSessionService.ActivePlayerId, _playerId);

        var trackKey = $"{state.Artist}\u001f{state.Title}\u001f{state.Album}";
        if (!string.Equals(trackKey, _lyricsTrackKey, StringComparison.Ordinal))
        {
            _lyricsTrackKey = trackKey;
            _lyrics = null;
            _songState = LyricsSongStateStore.Load(trackKey);
            _lyricsOffset = _songState.Offset;
            overlay.SetLyricsOffset(_lyricsOffset);
            overlay.SetLyricsLock(_songState.LockedSource is not null, _songState.LastSource);
            overlay.SetLyricsStatus(_songState.LastSource);
            overlay.UpdateMedia(state.Title, state.Artist, state.ArtworkBytes);
            StartLyricsLookup(state);
        }
        else if (!ReferenceEquals(_lastArtwork, state.ArtworkBytes))
        {
            overlay.UpdateMedia(state.Title, state.Artist, state.ArtworkBytes);
        }
        _lastArtwork = state.ArtworkBytes;
        overlay.UpdatePlaybackState(state.IsPlaying, state.CanPlayPause,
            state.CanGoPrevious, state.CanGoNext, state.CanSeek);
        overlay.UpdateProgress(
            _mediaSessionService.CurrentPosition, EffectiveDuration(state), state.IsPlaying);
        overlay.UpdateLyrics(_lyrics, _mediaSessionService.CurrentPosition);
        UpdateTimers(state);
    }

    private void StartLyricsLookup(MediaSessionState state, bool refetch = false)
    {
        _lyricsLookupCancellation?.Cancel();
        _lyricsLookupCancellation?.Dispose();
        _lyricsLookupCancellation = new CancellationTokenSource();
        _lyrics = null;
        if (_overlay is { } overlay)
            overlay.UpdateLyrics(null, _mediaSessionService.CurrentPosition);
        _ = FindLyricsAsync(_lyricsTrackKey, state, _lyricsSourceSettings,
            _songState.LockedSource, _lyricsLookupCancellation.Token, refetch);
    }

    private byte[]? _lastArtwork;

    /// <summary>
    /// Remembers which source produced the lyrics that are on screen, so the
    /// menu can show it and a later lookup keeps the same answer.
    /// </summary>
    private void RecordLyricsResult(LyricsDocument? result)
    {
        if (result?.Source is not { } source)
            return;

        _songState = _songState with
        {
            LastSource = source,
            FetchedAt = DateTimeOffset.Now
        };
        LyricsSongStateStore.Save(_lyricsTrackKey, _songState);
    }

    /// <summary>
    /// The length of the current track. The player's own timeline is preferred;
    /// when it publishes none (many players do not), the length that came with
    /// the lyrics is used instead.
    /// </summary>
    private TimeSpan EffectiveDuration(MediaSessionState state) =>
        state.Duration > TimeSpan.Zero ? state.Duration : _lyrics?.Duration ?? TimeSpan.Zero;

    private async Task FindLyricsAsync(
        string trackKey,
        MediaSessionState state,
        LyricsSourceSettings settings,
        LyricsSource? locked,
        CancellationToken token,
        bool refetch)
    {
        try
        {
            var request = new LyricsRequest(state.Title, state.Artist, state.Album, state.Duration);
            var result = refetch
                ? await _lyricsService.RefetchAsync(request, settings, locked, token)
                : await _lyricsService.FindAsync(request, settings, locked, token);
            if (token.IsCancellationRequested) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted)
                return;

            await dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || trackKey != _lyricsTrackKey || _overlay is not { } overlay)
                    return;
                _lyrics = result;
                RecordLyricsResult(result);
                overlay.SetLyricsStatus(_songState.LastSource);
                overlay.SetLyricsLock(_songState.LockedSource is not null, _songState.LastSource);
                overlay.UpdateLyrics(_lyrics, _mediaSessionService.CurrentPosition);
                // The lyrics may be the only source of the track length, so the
                // progress bar is refreshed as soon as they arrive.
                overlay.UpdateProgress(
                    _mediaSessionService.CurrentPosition,
                    EffectiveDuration(_latestMediaState),
                    _latestMediaState.IsPlaying);
                UpdateTimers(_latestMediaState);
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { /* A failed lyric lookup leaves the metadata visible. */ }
    }

    /// <summary>
    /// Keeps the remembered taskbar alignment in step with detection. The overlay
    /// raises the change itself once it is listening, but the very first
    /// placement happens before it can.
    /// </summary>
    private void RememberTaskbarCentered(bool centered)
    {
        if (_taskbarCentered == centered)
            return;

        _taskbarCentered = centered;
        SaveSettings();
    }

    private void OnTaskbarCenteredChanged(object? sender, bool centered)
    {
        // A change of the Windows taskbar alignment decides whether the widget
        // may sit in a left block at all, so it is remembered with the rest.
        if (_taskbarCentered == centered)
            return;

        _taskbarCentered = centered;
        SaveSettings();
    }

    private void OnBlockChanged(object? sender, WidgetBlock block)
    {
        _block = block;
        SaveSettings();
    }

    private void OnAlignmentChanged(object? sender, WidgetAlignment alignment)
    {
        _alignment = alignment;
        SaveSettings();
    }

    /// <summary>
    /// The tray icon is the widget's fallback entry point: the taskbar may have
    /// no room for it, and it is hidden while nothing plays.
    /// </summary>
    private void StartTrayIcon()
    {
        if (_trayIcon is not null)
            return;

        try
        {
            _trayIcon = new TrayIcon();
            _trayIcon.MenuRequested += OnTrayMenuRequested;
            _trayIcon.ToggleRequested += OnTrayToggleRequested;
        }
        catch (Exception)
        {
            // No notification area access is not a reason to stop the widget.
            _trayIcon = null;
        }
    }

    private void OnTrayMenuRequested(int screenX, int screenY)
    {
        if (_overlay is { } overlay)
        {
            overlay.ShowContextMenuAt(
                _trayIcon?.Handle ?? 0, screenX, screenY);
            return;
        }

        // Without an overlay there is no widget state to build the menu from,
        // but the tray must still be able to exit or start with Windows.
        using var menu = new Interop.NativeMenu();
        menu.AddItem("Start with Windows", 1, _startWithWindows);
        menu.AddItem("Exit Taskbar Music Widget", 2);
        var command = Interop.NativeMenu.Show(
            menu.Handle, _trayIcon?.Handle ?? 0, screenX, screenY);
        if (command == 1)
        {
            _startWithWindows = !_startWithWindows;
            AutostartStore.Set(_startWithWindows);
            SaveSettings();
        }
        else if (command == 2)
        {
            Shutdown();
        }
    }

    private void OnPlayersChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;

        _ = dispatcher.BeginInvoke(() =>
        {
            _overlay?.SetPlayerOptions(
                _mediaSessionService.Players, _mediaSessionService.ActivePlayerId, _playerId);
        });
    }

    private void OnPlayerSelectedChanged(object? sender, string? playerId)
    {
        _playerId = playerId ?? "";
        _mediaSessionService.SelectPlayer(_playerId);
        SaveSettings();
        if (_overlay is { } overlay)
            overlay.SetPlayerOptions(
                _mediaSessionService.Players, _mediaSessionService.ActivePlayerId, _playerId);
    }

    private void OnManualHiddenChanged(object? sender, bool hidden)
    {
        _manualHidden = hidden;
        _showForced = false;
        SaveSettings();
        UpdateVisibility();
    }

    private void OnPauseHideChanged(object? sender, int seconds)
    {
        _pauseHideSeconds = seconds;
        SaveSettings();
        UpdatePauseHide(_latestMediaState);
        UpdateVisibility();
    }

    /// <summary>
    /// Starts the countdown that hides the widget after a pause. It is only
    /// started once, so the timeline events a paused player keeps sending do not
    /// restart it.
    /// </summary>
    private void UpdatePauseHide(MediaSessionState state)
    {
        if (state.IsPlaying)
            _showForced = false;

        var paused = _mediaSessionService.HasSession && !state.IsPlaying;
        if (!paused || _pauseHideSeconds <= 0)
        {
            _pauseHideTimer.Stop();
            _pausedHidden = false;
            return;
        }

        if (_pauseHideTimer.IsEnabled || _pausedHidden)
            return;

        _pauseHideTimer.Interval = TimeSpan.FromSeconds(_pauseHideSeconds);
        _pauseHideTimer.Start();
    }

    private void OnPauseHideTimerTick(object? sender, EventArgs e)
    {
        _pauseHideTimer.Stop();
        if (!_mediaSessionService.HasSession ||
            _mediaSessionService.CurrentState.IsPlaying)
            return;

        _pausedHidden = true;
        UpdateVisibility();
    }

    /// <summary>
    /// One place decides whether the widget is on screen. It is hidden by hand,
    /// because no player has a session, or because playback has been paused for
    /// longer than the chosen timeout; the reason goes to the tray tooltip, which
    /// is the only part of the widget still visible then.
    /// </summary>
    private void UpdateVisibility()
    {
        string? reason = null;
        if (_manualHidden)
            reason = "by hand";
        else if (!_showForced && _hideWhenIdle && !_mediaSessionService.HasSession)
            reason = "no media";
        else if (!_showForced && _pausedHidden)
            reason = "paused";

        _widgetHidden = reason is not null;
        _overlay?.SetHidden(_widgetHidden, reason ?? "");
        _trayIcon?.SetTooltip(reason is null
            ? "Taskbar Music Widget"
            : $"Taskbar Music Widget - {reason}");
    }

    /// <summary>
    /// The tray icon's left click: hide the widget when it is on screen, bring it
    /// back when it is not - including when it was hidden for no media, where an
    /// explicit show wins until playback starts.
    /// </summary>
    private void OnTrayToggleRequested()
    {
        if (_widgetHidden)
        {
            _manualHidden = false;
            _pausedHidden = false;
            _showForced = true;
        }
        else
        {
            _manualHidden = true;
        }

        _overlay?.SetManualHidden(_manualHidden);
        SaveSettings();
        UpdateVisibility();
    }

    private void OnHideWhenIdleChanged(object? sender, bool enabled)
    {
        _hideWhenIdle = enabled;
        SaveSettings();
        UpdateVisibility();
    }

    private void OnStartupChanged(object? sender, bool enabled)
    {
        _startWithWindows = enabled;
        AutostartStore.Set(enabled);
        SaveSettings();
    }

    private void OnTranslationChanged(object? sender, bool enabled)
    {
        _showTranslation = enabled;
        SaveSettings();
    }

    private void OnLyricsModeChanged(object? sender, bool enabled)
    {
        _lyricsMode = enabled;
        SaveSettings();
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
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;

        await dispatcher.InvokeAsync(() =>
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
        // Nothing is playing anywhere: the poll and the sync timers have nothing
        // to do, so they stop until a player has a session again. Media events
        // bring the widget back, and a layout refresh catches up on any taskbar
        // change that happened while it was idle.
        if (!_mediaSessionService.HasSession)
        {
            if (_layoutTimer.IsEnabled)
            {
                _layoutTimer.Stop();
                _lyricsTimer.Stop();
                _timelineSyncTimer.Stop();
            }

            return;
        }

        if (!_layoutTimer.IsEnabled)
        {
            _layoutTimer.Start();
            _ = RefreshTaskbarLayoutAsync();
        }

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
    {
        if (_layoutTimer.Interval == ProbePollInterval && DateTime.UtcNow >= _probeUntil)
            EndLayoutProbe();

        _ = RefreshTaskbarLayoutAsync();
    }

    /// <summary>Switches the poll to the fast interval while the shell settles.</summary>
    private void StartLayoutProbe()
    {
        _probeStableScans = 0;
        _probeUntil = DateTime.UtcNow + ProbeDuration;
        if (_layoutTimer.Interval != ProbePollInterval)
            _layoutTimer.Interval = ProbePollInterval;
    }

    private void EndLayoutProbe()
    {
        _probeStableScans = 0;
        _probeUntil = DateTime.MinValue;
        _layoutTimer.Interval = NormalPollInterval;
    }

    private void OnTaskbarLayoutChanged(object? sender, EventArgs e)
        => ScheduleLayoutRefresh();

    private void OnTaskbarCreated(object? sender, EventArgs e)
        => ScheduleLayoutRefresh();

    private void ScheduleLayoutRefresh()
    {
        // The WinEvent and UIA callbacks can outlive the application: a hook can
        // fire while the widget is shutting down, when Application.Current is
        // already gone. Nothing to refresh then.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;

        _ = dispatcher.BeginInvoke(() =>
        {
            _layoutChangeTimer.Stop();
            _layoutChangeTimer.Start();
        });
    }

    private void OnLayoutChangeTimerTick(object? sender, EventArgs e)
    {
        _layoutChangeTimer.Stop();
        // Debounced: the WinEvent and UIA listeners fire in bursts, so this is
        // the one place a layout refresh gets recorded.
        TaskbarLayoutDiagnostics.Write("layout change settled: refreshing");
        StartLayoutProbe();
        _ = RefreshTaskbarLayoutAsync();
        ScheduleSettleRefreshes();
    }

    /// <summary>
    /// The shell commits the new taskbar layout slightly after the change is
    /// reported, so the first refresh can still measure the old one - closing an
    /// application leaves the widget narrow until something refreshes again.
    /// A few follow-ups catch the settled layout without waiting for the poll.
    /// </summary>
    private void ScheduleSettleRefreshes()
    {
        _settleRefreshesLeft = 3;
        // Compared against what is on screen now, so the first follow-up that
        // finds the same layout as the one before it can stop the rest.
        _settleLastAvailable = _positionedAvailableBounds;
        _settleRefreshTimer.Stop();
        _settleRefreshTimer.Start();
    }

    private void OnSettleRefreshTimerTick(object? sender, EventArgs e)
    {
        if (--_settleRefreshesLeft <= 0)
            _settleRefreshTimer.Stop();

        _ = RefreshTaskbarLayoutAsync();
    }

    /// <summary>
    /// Stops the follow-up refreshes as soon as two detections in a row agree:
    /// the layout has settled and further scans would only cost CPU. Each scan is
    /// a UI Automation walk of the taskbar, so they are not free.
    /// </summary>
    private void NoteDetectedLayout(Rect blockBounds)
    {
        // Movement of the icon group produces no event, so a detection that
        // differs from the applied placement buys another probe window. The
        // block that matters is the one the widget is pinned to: with a left
        // aligned widget the right block can stay put while the left one grows.
        if (blockBounds != _positionedBlockBounds)
        {
            StartLayoutProbe();
        }
        else if (_layoutTimer.Interval == ProbePollInterval &&
                 ++_probeStableScans >= ProbeStableScansToStop)
        {
            EndLayoutProbe();
        }

        if (_settleRefreshesLeft <= 0)
            return;

        if (blockBounds == _settleLastAvailable)
        {
            _settleRefreshesLeft = 0;
            _settleRefreshTimer.Stop();
            return;
        }

        _settleLastAvailable = blockBounds;
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
                DiscardMissingSelection();
                var detectStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                var layouts = await Task.Run(DetectTaskbarLayouts);
                var detectMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(
                    detectStarted).TotalMilliseconds;
                _taskbarOptions = layouts.Select(entry => new TaskbarOption(
                    entry.Taskbar.DeviceId,
                    entry.Taskbar.DisplayName,
                    entry.Taskbar.IsPrimary)).ToArray();
                _overlay?.SetTaskbarOptions(_taskbarOptions, _selectedTaskbarDeviceId);
                TaskbarLayoutDiagnostics.Write(
                    $"layout detected ({detectMilliseconds:F1} ms): available=" +
                    string.Join(";", layouts.Select(entry => entry.Layout.AvailableBounds)));
                if (layouts.Length > 0)
                {
                    var first = OrderForSelection(layouts)[0].Layout;
                    NoteDetectedLayout(ChosenBlock(first));
                }
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
                        RectsNearEqual(layout.AvailableBounds, _positionedAvailableBounds) &&
                        RectsNearEqual(layout.LeftBlockBounds, _positionedLeftBlockBounds);

                    if (!samePlacement)
                    {
                        TaskbarLayoutDiagnostics.Write(
                            $"reposition overlay: taskbar={taskbar} " +
                            $"available={layout.AvailableBounds} " +
                            $"leftBlock={layout.LeftBlockBounds}");
                        var position = _overlay.PositionInAvailableArea(
                            taskbar,
                            layout.TaskbarBounds,
                            layout.AvailableBounds,
                            layout.LeftBlockBounds,
                            layout.Centered);
                        if (position != TaskbarPositionResult.Positioned)
                        {
                            TaskbarLayoutDiagnostics.Write(
                                $"hide overlay: failed to position in available area ({position})");
                            ReportPositionFailure(position, taskbar);
                            _trayIcon?.SetTooltip(
                                "Taskbar Music Widget - no room on the taskbar");
                            _overlay.Hide();
                            break;
                        }

                        _positionedTaskbar = taskbar;
                        _positionedTaskbarBounds = layout.TaskbarBounds;
                        _positionedAvailableBounds = layout.AvailableBounds;
                        _positionedLeftBlockBounds = layout.LeftBlockBounds;
                        _positionedCentered = layout.Centered;
                        _positionedBlockBounds = ChosenBlock(layout);
                        RememberTaskbarCentered(layout.Centered);

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
        catch (Exception ex)
        {
            // The refresh runs detached, so an unhandled exception would be lost
            // and the widget would silently keep a stale layout.
            TaskbarLayoutDiagnostics.WriteError(
                $"layout refresh failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _layoutRefreshInProgress = false;
        }
    }

    private void ReportPositionFailure(TaskbarPositionResult result, IntPtr taskbarHandle)
    {
        if (result != TaskbarPositionResult.NotAttached)
            return;

        TaskbarLayoutDiagnostics.WriteError(
            $"the widget could not be attached to the taskbar window {taskbarHandle}; " +
            "explorer probably runs with different privileges than this process");
        if (_attachmentFailureReported)
            return;

        _attachmentFailureReported = true;
        try
        {
            MessageBox.Show(
                "无法把组件挂接到任务栏。\n\n" +
                "Windows 会阻止权限级别不同的进程之间挂接窗口：如果资源管理器" +
                "（Explorer）以管理员身份运行，本组件也需要以管理员身份运行；" +
                "反之亦然。\n\n" +
                "组件已经隐藏，请调整权限后重新启动。",
                "Taskbar Music Controls",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception)
        {
            // A missing message box must never take the widget down.
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

    /// <summary>
    /// Compared with a tolerance rather than exactly: the shell animates the task
    /// list, so the available area arrives in small steps. Repositioning for each
    /// step repaints the widget several times in a row; the steps are ignored and
    /// the settled layout is the one that gets applied.
    /// </summary>
    private static bool RectsNearEqual(Rect left, Rect right)
    {
        // Rect.Empty is made of infinities, so |inf - inf| is NaN and every
        // comparison with NaN is false: two empty rects - no left block, for
        // instance - compared as "not near" and made every poll reposition the
        // widget, which repaints it.
        if (left.IsEmpty || right.IsEmpty)
            return left.IsEmpty && right.IsEmpty;

        const double tolerance = 24;
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

    /// <summary>
    /// Drops a selection whose taskbar no longer exists - the monitor was
    /// unplugged or disabled - and moves to the primary one, so the widget keeps
    /// showing somewhere instead of waiting for a taskbar that will not come
    /// back. A taskbar that exists but whose UIA walk failed is kept: that is
    /// temporary and the selection should return to it.
    /// </summary>
    private void DiscardMissingSelection()
    {
        if (_selectedTaskbarDeviceId.Length == 0)
            return;

        var taskbars = _taskbarDetector.DetectTaskbars();
        if (taskbars.Count == 0)
            return;

        if (taskbars.Any(taskbar => string.Equals(
                taskbar.DeviceId, _selectedTaskbarDeviceId, StringComparison.Ordinal)))
            return;

        var fallback = taskbars.FirstOrDefault(taskbar => taskbar.IsPrimary) ?? taskbars[0];
        TaskbarLayoutDiagnostics.Write(
            $"selected taskbar {_selectedTaskbarDeviceId} is gone: " +
            $"falling back to {fallback.DeviceId}");
        _selectedTaskbarDeviceId = fallback.DeviceId;
        _overlay?.SetTaskbarSelection(_selectedTaskbarDeviceId);
        SaveSettings();
    }

    /// <summary>The block the widget is pinned to, which is what it follows.</summary>
    private Rect ChosenBlock(TaskbarLayoutDetector.TaskbarLayout layout) =>
        _block == WidgetBlock.Left && !layout.LeftBlockBounds.IsEmpty
            ? layout.LeftBlockBounds
            : layout.AvailableBounds;

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
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;

        // The window is a child of the taskbar, so it is destroyed together with
        // it: turning off the monitor that hosts the taskbar, or restarting
        // Explorer, closes the widget as well. Only the Exit command means the
        // widget itself should end; anything else re-attaches to a taskbar that
        // still exists.
        if (_exitRequested || sender is MusicOverlayWindow { ExitRequested: true })
        {
            _exitRequested = true;
            Shutdown();
            return;
        }

        TaskbarLayoutDiagnostics.Write(
            "overlay was closed with its taskbar: looking for another one");
        if (ReferenceEquals(_overlay, sender))
            _overlay = null;

        _ = RefreshTaskbarLayoutAsync();
    }

    private void Shutdown()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
        _layoutTimer.Stop();
        _layoutChangeTimer.Stop();
        _nativeHandleTimer.Stop();
        _taskbarRecoveryTimer.Stop();
        _lyricsTimer.Stop();
        _timelineSyncTimer.Stop();
        _pauseHideTimer.Stop();
        _lyricsLookupCancellation?.Cancel();
        _lyricsLookupCancellation?.Dispose();
        Application.Current.Shutdown();
    }

    private void WatchTaskbar(IntPtr taskbarHandle)
    {
        if (_watchedTaskbar == taskbarHandle)
            return;

        // The WinEvent hook in TaskbarWindowMonitor covers changes that own a
        // window; the UIA listener covers the ones that do not, such as a
        // taskbar button appearing or a tray icon moving into the hidden icons.
        _watchedTaskbar = taskbarHandle;
        _layoutMonitor.Watch(taskbarHandle);
    }
}
