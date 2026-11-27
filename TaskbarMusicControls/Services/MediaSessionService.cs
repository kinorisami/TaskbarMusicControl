using System.Diagnostics;
using System.IO;
using TaskbarMusicControls.Interop;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace TaskbarMusicControls.Services;

internal sealed class MediaSessionService
{
    private const uint MaxArtworkBytes = 2 * 1024 * 1024;

    // Players announce the title of the next track before its artwork is
    // ready, so the first thumbnail of a new track can still carry the cover
    // of the previous one. The artwork is therefore read again for a short
    // while after a track change instead of being pinned to the track for its
    // whole duration.
    private static readonly TimeSpan ArtworkSettleInterval = TimeSpan.FromSeconds(2);
    private const int ArtworkSettleAttempts = 8;

    // Reading the image on every metadata event is cheap, but players hand out
    // a new thumbnail reference on every event, so the reads are throttled to
    // keep a burst of events from reading the same image over and over.
    private static readonly TimeSpan ArtworkReadInterval = TimeSpan.FromSeconds(2);

    // Right after a track change the player announces the title first and the
    // artwork in a follow-up event, so the reads in that window are not
    // throttled - the artwork of the new track would be missed otherwise.
    private static readonly TimeSpan ArtworkUnthrottledWindow = TimeSpan.FromSeconds(3);
    private const int ArtworkUnthrottledReads = 4;

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly PlaybackClock _playbackClock = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _clockTrackIdentity = "";
    private readonly object _artworkGate = new();
    private string _artworkIdentity = "";
    private IRandomAccessStreamReference? _artworkThumbnail;
    private DateTimeOffset _lastArtworkReadAt = DateTimeOffset.MinValue;
    private DateTimeOffset _artworkTrackChangedAt = DateTimeOffset.MinValue;
    private int _artworkReadsForTrack;
    private bool _artworkAwaitingUpdate;
    private long _artworkReadSequence;
    private long _publishedArtworkSequence;
    private string _artworkSettleIdentity = "";
    private CancellationTokenSource? _artworkSettleCancellation;

    public event Action<MediaSessionState>? StateChanged;
    public MediaSessionState CurrentState { get; private set; } = MediaSessionState.Empty;
    public TimeSpan CurrentPosition => _playbackClock.GetCurrentPosition();

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            _manager.SessionsChanged += OnSessionsChanged;
            // Same reason as the event handlers: the first property reads and the
            // thumbnail of the current session must not run on the UI thread.
            await Task.Run(() => RefreshCurrentSessionAsync());
        }
        catch (Exception)
        {
            Publish(MediaSessionState.Empty);
        }
    }

    // GSMTC is a synchronous COM call into the player: when the player is busy -
    // which is exactly when a track changes - it blocks the calling thread for
    // hundreds of milliseconds. Everything that talks to the session therefore
    // runs on the thread pool, never on the UI thread.
    public Task SkipPreviousAsync() =>
        Task.Run(() => RunCommandAsync(session => session.TrySkipPreviousAsync()));

    public Task RefreshTimelineNowAsync()
    {
        var session = _session;
        return session is null ? Task.CompletedTask : Task.Run(() => RefreshTimelineAsync(session));
    }

    public Task TogglePlaybackAsync() =>
        Task.Run(() => RunCommandAsync(session => session.TryTogglePlayPauseAsync()));

    public Task SkipNextAsync() =>
        Task.Run(() => RunCommandAsync(session => session.TrySkipNextAsync()));

    /// <summary>
    /// Shows or hides the window of the player behind the current session. A
    /// Win32 player reports its executable as the session id, which is enough to
    /// find its main window; for a packaged app there is no reliable way to get
    /// there, so nothing happens.
    /// </summary>
    /// <remarks>
    /// The second click puts the window away again. Which click is which is
    /// tracked by this process rather than by asking Windows for the foreground
    /// window: the widget cannot be activated, so the foreground window is not a
    /// reliable hint by the time the click arrives.
    /// </remarks>
    public void TogglePlayerWindow()
    {
        var sessionId = _session?.SourceAppUserModelId;
        if (string.IsNullOrWhiteSpace(sessionId) ||
            !sessionId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var processName = Path.GetFileNameWithoutExtension(sessionId);
            var processes = Process.GetProcessesByName(processName);
            if (processes.Length == 0)
                return;

            var processIds = processes.Select(process => (uint)process.Id).ToHashSet();

            // Process.MainWindowHandle only reports a visible, unowned window, so
            // it is 0 while the player sits in the tray. Enumerate the process's
            // windows instead and prefer the visible one.
            var handle = FindPlayerWindow(processIds, requireVisible: true);
            if (handle == 0)
                handle = FindPlayerWindow(processIds, requireVisible: false);
            if (handle == 0)
                return;

            // Clicking the cover a second time puts the window away again; a
            // player that was closed by hand is simply shown on the next click.
            if (handle == _shownPlayerWindow && NativeMethods.IsWindowVisible(handle))
            {
                NativeMethods.ShowWindow(handle, NativeMethods.SwHide);
                _shownPlayerWindow = nint.Zero;
                return;
            }

            NativeMethods.ShowWindow(handle,
                NativeMethods.IsIconic(handle) ? NativeMethods.SwRestore : NativeMethods.SwShow);
            NativeMethods.SetForegroundWindow(handle);
            _shownPlayerWindow = handle;
        }
        catch (Exception)
        {
            // A player that refuses to come forward is not worth an error.
        }
    }

    private nint _shownPlayerWindow;

    /// <summary>
    /// The player's main window: the largest titled top-level window of its
    /// process. The largest one is used so a small hidden helper window cannot be
    /// picked instead of the real player.
    /// </summary>
    private static nint FindPlayerWindow(HashSet<uint> processIds, bool requireVisible)
    {
        var result = nint.Zero;
        long bestArea = 0;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (!processIds.Contains(processId))
                return true;

            if (NativeMethods.GetWindow(hwnd, NativeMethods.GwOwner) != nint.Zero)
                return true;

            if (NativeMethods.GetWindowTextLength(hwnd) == 0)
                return true;

            if (requireVisible && !NativeMethods.IsWindowVisible(hwnd))
                return true;

            if (!NativeMethods.GetWindowRect(hwnd, out var rect))
                return true;

            if (rect.Area <= bestArea)
                return true;

            bestArea = rect.Area;
            result = hwnd;
            return true;
        }, nint.Zero);
        return result;
    }

    public Task SeekAsync(TimeSpan position) => Task.Run(() => SeekCoreAsync(position));

    private async Task SeekCoreAsync(TimeSpan position)
    {
        var session = _session;
        if (session is null)
            return;

        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;

        try
        {
            await session.TryChangePlaybackPositionAsync(position.Ticks);
        }
        catch (Exception)
        {
            // A player that refuses the seek simply keeps playing from where it
            // was; the clock is left alone.
            return;
        }

        // Move the clock right away instead of waiting for the player to report
        // the new position back.
        _playbackClock.SeekTo(position);
        await RefreshTimelineNowAsync();
    }

    private async Task RunCommandAsync(
        Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> command)
    {
        var session = _session;
        if (session is null)
            return;

        try
        {
            await command(session);
            await RefreshPlaybackInfoAsync(session);
        }
        catch (Exception)
        {
            await RefreshCurrentSessionAsync();
        }
    }

    private void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args) =>
        _ = Task.Run(() => RefreshCurrentSessionAsync());

    private void OnSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args) =>
        _ = Task.Run(() => RefreshCurrentSessionAsync());

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args) =>
        _ = Task.Run(() => RefreshMediaPropertiesAsync(sender));

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args) =>
        _ = Task.Run(() => RefreshPlaybackInfoAsync(sender));

    private void OnTimelinePropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args) =>
        _ = Task.Run(() => RefreshTimelineAsync(sender));

    private async Task RefreshCurrentSessionAsync()
    {
        GlobalSystemMediaTransportControlsSession? session;
        await _refreshLock.WaitAsync();
        try
        {
            var nextSession = _manager?.GetCurrentSession();
            if (!ReferenceEquals(_session, nextSession))
            {
                if (_session is not null)
                {
                    _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                    _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                    _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
                }

                _session = nextSession;
                _clockTrackIdentity = "";
                _playbackClock.Reset("session-changed");
                ResetArtworkState();
                if (_session is not null)
                {
                    _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                    _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                    _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
                }
            }

            session = _session;
        }
        catch (Exception)
        {
            Publish(MediaSessionState.Empty);
            return;
        }
        finally
        {
            _refreshLock.Release();
        }

        try
        {
            await PublishSessionStateAsync(session);
        }
        catch (Exception)
        {
            Publish(MediaSessionState.Empty);
        }
    }

    private async Task RefreshMediaPropertiesAsync(
        GlobalSystemMediaTransportControlsSession changedSession)
    {
        string trackIdentity;
        IRandomAccessStreamReference? thumbnail;
        bool trackChanged;

        await _refreshLock.WaitAsync();
        try
        {
            if (!ReferenceEquals(changedSession, _session))
                return;

            // The event does not carry the properties themselves, so they are
            // fetched. The fetch happens right after the event, so it returns
            // the state of the player at that moment - which already carries
            // the new cover when the player published it with the new title.
            var properties = await changedSession.TryGetMediaPropertiesAsync();
            var timeline = changedSession.GetTimelineProperties();
            if (!ReferenceEquals(changedSession, _session))
                return;

            trackIdentity = BuildTrackIdentity(properties.Title, properties.Artist, properties.AlbumTitle);
            trackChanged = trackIdentity != _clockTrackIdentity;
            if (trackChanged)
            {
                // The cover on screen belongs to the previous track until the
                // cover of the new one arrives.
                MarkArtworkAwaitingUpdate();
            }
            ResetClockIfTrackChanged(properties.Title, properties.Artist, properties.AlbumTitle);
            _playbackClock.Synchronize(timeline, CurrentState.IsPlaying, timeline.EndTime,
                "media-properties");

            Publish(CurrentState with
            {
                Title = properties.Title,
                Artist = properties.Artist,
                Album = properties.AlbumTitle,
                // The cover of the new track arrives a moment after its title.
                // The cover that is on screen is carried over on purpose: it
                // avoids a flash of the placeholder and lets the new cover
                // replace it in one step.
                Duration = timeline.EndTime > TimeSpan.Zero ? timeline.EndTime : CurrentState.Duration
            });

            thumbnail = properties.Thumbnail;
        }
        catch (Exception)
        {
            // Keep the last good metadata when a player temporarily stops responding.
            return;
        }
        finally
        {
            _refreshLock.Release();
        }

        await RefreshArtworkAsync(changedSession, trackIdentity, thumbnail);
        if (trackChanged)
            StartArtworkSettle(changedSession, trackIdentity);
    }

    private async Task RefreshArtworkAsync(
        GlobalSystemMediaTransportControlsSession session,
        string trackIdentity,
        IRandomAccessStreamReference? thumbnail,
        bool force = false)
    {
        if (!force && !ShouldReadArtwork(trackIdentity, thumbnail))
            return;

        // A new reference means the player published something. When its
        // content is the cover that is already on screen, that is the cover of
        // this track and there is nothing left to wait for.
        bool referenceChanged;
        lock (_artworkGate)
        {
            referenceChanged = !ReferenceEquals(thumbnail, _artworkThumbnail);
        }

        // Two reads can be in flight at the same time: the one that started
        // first may still carry the cover of the previous track. The sequence
        // makes sure that the newest read is the one that reaches the widget.
        var sequence = Interlocked.Increment(ref _artworkReadSequence);

        var read = await ReadArtworkAsync(thumbnail);
        if (!read.Succeeded)
        {
            ArtworkDiagnostics.WriteError(
                $"read failed track={DescribeTrack(trackIdentity)}");
            return;
        }

        ArtworkDiagnostics.Write(
            $"read track={DescribeTrack(trackIdentity)} bytes={read.Bytes?.Length ?? 0}");
        lock (_artworkGate)
        {
            _artworkThumbnail = thumbnail;
            _lastArtworkReadAt = DateTimeOffset.UtcNow;
            if (!force)
            {
                // Only the reads that a metadata event triggered count against
                // the unthrottled window of a track change.
                _artworkReadsForTrack++;
            }
        }

        await PublishArtworkIfCurrentAsync(
            session, trackIdentity, read.Bytes, sequence, referenceChanged);
    }

    private async Task RefreshPlaybackInfoAsync(
        GlobalSystemMediaTransportControlsSession changedSession)
    {
        await _refreshLock.WaitAsync();
        try
        {
            if (!ReferenceEquals(changedSession, _session))
                return;

            var playbackInfo = changedSession.GetPlaybackInfo();
            var controls = playbackInfo.Controls;
            var isPlaying = playbackInfo.PlaybackStatus ==
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var canPlayPause = controls.IsPlayPauseToggleEnabled ||
                (isPlaying ? controls.IsPauseEnabled : controls.IsPlayEnabled);
            var timeline = changedSession.GetTimelineProperties();
            _playbackClock.Synchronize(timeline, isPlaying,
                timeline.EndTime > TimeSpan.Zero ? timeline.EndTime : CurrentState.Duration,
                "playback-state");

            if (!ReferenceEquals(changedSession, _session))
                return;

            Publish(CurrentState with
            {
                IsPlaying = isPlaying,
                CanPlayPause = canPlayPause,
                CanGoPrevious = controls.IsPreviousEnabled,
                CanGoNext = controls.IsNextEnabled,
                CanSeek = controls.IsPlaybackPositionEnabled,
                Duration = timeline.EndTime > TimeSpan.Zero ? timeline.EndTime : CurrentState.Duration
            });
        }
        catch (Exception)
        {
            // Keep the last good playback state when a player temporarily stops responding.
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task RefreshTimelineAsync(
        GlobalSystemMediaTransportControlsSession changedSession)
    {
        await _refreshLock.WaitAsync();
        try
        {
            if (!ReferenceEquals(changedSession, _session))
                return;
            var timeline = changedSession.GetTimelineProperties();
            var duration = timeline.EndTime > TimeSpan.Zero ? timeline.EndTime : CurrentState.Duration;
            _playbackClock.Synchronize(timeline, CurrentState.IsPlaying, duration,
                "timeline-event-or-poll");
            Publish(CurrentState with
            {
                Duration = duration
            });
        }
        catch (Exception)
        {
            // Timeline data is optional.
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task PublishSessionStateAsync(
        GlobalSystemMediaTransportControlsSession? session)
    {
        if (session is null)
        {
            await PublishEmptySessionIfCurrentAsync();
            return;
        }

        var properties = await session.TryGetMediaPropertiesAsync();
        var playbackInfo = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();
        var isPlaying = playbackInfo.PlaybackStatus ==
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var controls = playbackInfo.Controls;
        var title = properties.Title;
        var artist = properties.Artist;
        var album = properties.AlbumTitle;
        var trackIdentity = BuildTrackIdentity(title, artist, album);
        var thumbnail = properties.Thumbnail;

        await _refreshLock.WaitAsync();
        try
        {
            if (!ReferenceEquals(session, _session))
                return;

            var trackChanged = trackIdentity != _clockTrackIdentity;
            ResetClockIfTrackChanged(title, artist, album);
            if (trackChanged)
            {
                // The cover on screen belongs to the previous track until the
                // cover of the new one arrives.
                MarkArtworkAwaitingUpdate();
            }
            _playbackClock.Synchronize(timeline, isPlaying, timeline.EndTime, "session-start");
            Publish(new MediaSessionState(
                title,
                artist,
                GetArtworkForIdentity(trackIdentity),
                isPlaying,
                controls.IsPlayPauseToggleEnabled ||
                    (playbackInfo.PlaybackStatus ==
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                        ? controls.IsPauseEnabled
                        : controls.IsPlayEnabled),
                controls.IsPreviousEnabled,
                controls.IsNextEnabled,
                album,
                timeline.EndTime,
                controls.IsPlaybackPositionEnabled));
        }
        finally
        {
            _refreshLock.Release();
        }

        // Artwork is loaded after committing metadata and the playback anchor so slow
        // thumbnail reads never hold up playback, timeline, or session-switch refreshes.
        await RefreshArtworkAsync(session, trackIdentity, thumbnail);
        StartArtworkSettle(session, trackIdentity);
    }

    private async Task PublishEmptySessionIfCurrentAsync()
    {
        await _refreshLock.WaitAsync();
        try
        {
            if (_session is null)
                Publish(MediaSessionState.Empty);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task PublishArtworkIfCurrentAsync(
        GlobalSystemMediaTransportControlsSession session,
        string trackIdentity,
        byte[]? artwork,
        long sequence,
        bool referenceChanged)
    {
        await _refreshLock.WaitAsync();
        try
        {
            if (!ReferenceEquals(session, _session) ||
                _clockTrackIdentity != trackIdentity)
                return;

            // A read that started earlier must not overwrite the cover that a
            // newer read already brought in.
            if (sequence < _publishedArtworkSequence)
                return;

            if (!TryUpdateArtwork(trackIdentity, artwork, referenceChanged))
                return;

            _publishedArtworkSequence = sequence;
            Publish(CurrentState with { ArtworkBytes = artwork });
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Decides whether the artwork that was just read has to reach the widget.
    /// The bytes are compared with the ones on screen so an unchanged cover is
    /// not decoded and rendered again for every metadata event.
    /// </summary>
    private bool TryUpdateArtwork(
        string trackIdentity,
        byte[]? artwork,
        bool referenceChanged)
    {
        lock (_artworkGate)
        {
            var identityChanged =
                !string.Equals(trackIdentity, _artworkIdentity, StringComparison.Ordinal);
            if (identityChanged)
            {
                _artworkIdentity = trackIdentity;

                if (artwork is null)
                {
                    _artworkAwaitingUpdate = false;
                    ArtworkDiagnostics.Write(
                        $"cover cleared track={DescribeTrack(trackIdentity)}");
                    return true;
                }

                if (CurrentState.ArtworkBytes is { } onScreen &&
                    onScreen.AsSpan().SequenceEqual(artwork))
                {
                    // This is still the cover of the previous track. It stays on
                    // screen until the cover of the new track shows up.
                    ArtworkDiagnostics.Write(
                        $"still the previous cover track={DescribeTrack(trackIdentity)} " +
                        $"bytes={artwork.Length}");

                    if (referenceChanged)
                    {
                        // The player published a new thumbnail whose content is
                        // the cover that is already on screen, so that is the
                        // cover of this track: there is nothing left to wait for.
                        _artworkAwaitingUpdate = false;
                    }

                    return false;
                }

                _artworkAwaitingUpdate = false;
                ArtworkDiagnostics.Write(
                    $"cover of a new track track={DescribeTrack(trackIdentity)} " +
                    $"bytes={artwork.Length}");
                return true;
            }

            if (artwork is null)
            {
                if (CurrentState.ArtworkBytes is null)
                    return false;
                _artworkAwaitingUpdate = false;
                ArtworkDiagnostics.Write(
                    $"cover cleared track={DescribeTrack(trackIdentity)}");
                return true;
            }

            if (CurrentState.ArtworkBytes is null ||
                !CurrentState.ArtworkBytes.AsSpan().SequenceEqual(artwork))
            {
                _artworkAwaitingUpdate = false;
                ArtworkDiagnostics.Write(
                    $"cover changed track={DescribeTrack(trackIdentity)} bytes={artwork.Length}");
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// The cover that belongs to the given track. The cover of another track
    /// is not carried over to a session that publishes a different song.
    /// </summary>
    private byte[]? GetArtworkForIdentity(string trackIdentity)
    {
        lock (_artworkGate)
        {
            return string.Equals(trackIdentity, _artworkIdentity, StringComparison.Ordinal)
                ? CurrentState.ArtworkBytes
                : null;
        }
    }

    /// <summary>
    /// Whether the thumbnail has to be read again. The artwork is bound to the
    /// thumbnail source and not to the track: a player announces the title of
    /// the next track before its artwork is ready, so the first thumbnail of a
    /// new track can still carry the cover of the previous one.
    /// </summary>
    private bool ShouldReadArtwork(
        string trackIdentity,
        IRandomAccessStreamReference? thumbnail)
    {
        lock (_artworkGate)
        {
            if (!string.Equals(trackIdentity, _artworkIdentity, StringComparison.Ordinal))
            {
                // A new track starts the unthrottled window.
                _artworkReadsForTrack = 0;
                _artworkTrackChangedAt = DateTimeOffset.UtcNow;
                return true;
            }

            if (ReferenceEquals(thumbnail, _artworkThumbnail))
                return false;

            // Right after a track change the player announces the title first
            // and the artwork in a follow-up event. The reads in that window
            // are not throttled, otherwise the artwork of the new track would
            // be missed and the previous cover would stay on screen.
            if (_artworkReadsForTrack < ArtworkUnthrottledReads &&
                DateTimeOffset.UtcNow - _artworkTrackChangedAt < ArtworkUnthrottledWindow)
                return true;

            // Players hand out a new reference on every metadata change, so the
            // reads are throttled to keep a burst of events from reading the
            // same image over and over again.
            return DateTimeOffset.UtcNow - _lastArtworkReadAt >= ArtworkReadInterval;
        }
    }

    private async Task<(bool Succeeded, byte[]? Bytes)> ReadArtworkAsync(
        IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null)
            return (true, null);

        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            if (stream.Size == 0)
                return (true, null);

            var byteCount = (uint)Math.Min(stream.Size, MaxArtworkBytes);
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            var loadedBytes = await reader.LoadAsync(byteCount);
            if (loadedBytes == 0)
                return (true, null);

            var bytes = new byte[loadedBytes];
            reader.ReadBytes(bytes);
            return (true, bytes);
        }
        catch (Exception)
        {
            // A failed read keeps the cover that is on screen: the player may
            // still be writing the image.
            return (false, null);
        }
    }

    /// <summary>
    /// Marks the cover on screen as belonging to the previous track: the cover
    /// of the new track has not arrived yet.
    /// </summary>
    private void MarkArtworkAwaitingUpdate()
    {
        lock (_artworkGate)
        {
            _artworkAwaitingUpdate = true;
        }
    }

    private bool IsArtworkAwaitingUpdate()
    {
        lock (_artworkGate)
        {
            return _artworkAwaitingUpdate;
        }
    }

    private void StartArtworkSettle(
        GlobalSystemMediaTransportControlsSession session,
        string trackIdentity)
    {
        var previous = _artworkSettleCancellation;
        var cancellation = new CancellationTokenSource();

        lock (_artworkGate)
        {
            if (string.Equals(trackIdentity, _artworkSettleIdentity, StringComparison.Ordinal))
            {
                cancellation.Dispose();
                return;
            }

            _artworkSettleIdentity = trackIdentity;
            _artworkSettleCancellation = cancellation;
        }

        // The previous sweep is stopped outside the lock: cancelling runs the
        // callback of its pending delay.
        previous?.Cancel();
        previous?.Dispose();

        _ = SettleArtworkAsync(session, trackIdentity, cancellation.Token);
    }

    private void CancelArtworkSettle()
    {
        CancellationTokenSource? previous;
        lock (_artworkGate)
        {
            previous = _artworkSettleCancellation;
            _artworkSettleCancellation = null;
        }

        // Cancelling runs the callback of the pending delay, which must not
        // happen while the lock is held.
        previous?.Cancel();
        previous?.Dispose();
    }

    /// <summary>
    /// Drops everything the artwork pipeline knows. A new session brings its
    /// own cover, so the cached one of the previous session must not be reused
    /// - otherwise the same song played again would look like it has no cover.
    /// </summary>
    private void ResetArtworkState()
    {
        CancelArtworkSettle();
        lock (_artworkGate)
        {
            _artworkSettleIdentity = "";
            _artworkIdentity = "";
            _artworkThumbnail = null;
            _artworkReadsForTrack = 0;
            _artworkTrackChangedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Keeps reading the artwork for a short while after a track change. The
    /// player publishes the new cover once it is available, which can happen
    /// after the title was already announced.
    /// </summary>
    private async Task SettleArtworkAsync(
        GlobalSystemMediaTransportControlsSession session,
        string trackIdentity,
        CancellationToken token)
    {
        for (var attempt = 0; attempt < ArtworkSettleAttempts; attempt++)
        {
            try
            {
                await Task.Delay(ArtworkSettleInterval, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested)
                return;

            // The cover of the new track has arrived: there is nothing left to
            // poll for, which keeps the number of thumbnail reads per track
            // change down.
            if (!IsArtworkAwaitingUpdate())
                return;

            try
            {
                // The properties are fetched again because the player may have
                // replaced the thumbnail reference after the last event.
                var properties = await session.TryGetMediaPropertiesAsync();
                await RefreshArtworkAsync(
                    session, trackIdentity, properties.Thumbnail, force: true);
            }
            catch (Exception)
            {
                // A player that stops responding must not break the widget.
            }
        }
    }

    private void Publish(MediaSessionState state)
    {
        CurrentState = state;
        StateChanged?.Invoke(state);
    }

    private void ResetClockIfTrackChanged(string title, string artist, string album)
    {
        var identity = BuildTrackIdentity(title, artist, album);
        if (identity == _clockTrackIdentity)
            return;
        _clockTrackIdentity = identity;
        _playbackClock.Reset("track-changed");
    }

    /// <summary>
    /// The track identity in a readable form for the diagnostics log.
    /// </summary>
    private static string DescribeTrack(string trackIdentity) =>
        trackIdentity.Replace((char)0x1f, '|');

    private static string BuildTrackIdentity(string title, string artist, string album) =>
        $"{artist}\u001f{title}\u001f{album}";

}

internal sealed record MediaSessionState(
    string Title,
    string Artist,
    byte[]? ArtworkBytes,
    bool IsPlaying,
    bool CanPlayPause,
    bool CanGoPrevious,
    bool CanGoNext,
    string Album = "",
    TimeSpan Duration = default,
    /// <summary>Whether the player accepts a jump to a playback position.</summary>
    bool CanSeek = false)
{
    public static MediaSessionState Empty { get; } = new(
        "No media playing",
        "Start playback in a media app",
        null,
        false,
        false,
        false,
        false);
}
