using Windows.Media.Control;
using Windows.Storage.Streams;

namespace TaskbarMusicControls.Services;

internal sealed class MediaSessionService
{
    private const uint MaxArtworkBytes = 8 * 1024 * 1024;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly PlaybackClock _playbackClock = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _clockTrackIdentity = "";

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
            await RefreshCurrentSessionAsync();
        }
        catch (Exception)
        {
            Publish(MediaSessionState.Empty);
        }
    }

    public Task SkipPreviousAsync() => RunCommandAsync(session => session.TrySkipPreviousAsync());

    public Task RefreshTimelineNowAsync()
    {
        var session = _session;
        return session is null
            ? Task.CompletedTask
            : RefreshTimelineAsync(session);
    }

    public Task TogglePlaybackAsync() => RunCommandAsync(session => session.TryTogglePlayPauseAsync());

    public Task SkipNextAsync() => RunCommandAsync(session => session.TrySkipNextAsync());

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
        _ = RefreshCurrentSessionAsync();

    private void OnSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args) =>
        _ = RefreshCurrentSessionAsync();

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args) =>
        _ = RefreshMediaPropertiesAsync(sender);

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args) =>
        _ = RefreshPlaybackInfoAsync(sender);

    private void OnTimelinePropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args) =>
        _ = RefreshTimelineAsync(sender);

    private async Task RefreshCurrentSessionAsync()
    {
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
                if (_session is not null)
                {
                    _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                    _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                    _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
                }
            }

            await PublishSessionStateAsync(_session);
        }
        catch (Exception)
        {
            Publish(MediaSessionState.Empty);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task RefreshMediaPropertiesAsync(
        GlobalSystemMediaTransportControlsSession changedSession)
    {
        await _refreshLock.WaitAsync();
        try
        {
            if (!ReferenceEquals(changedSession, _session))
                return;

            var properties = await changedSession.TryGetMediaPropertiesAsync();
            var timeline = changedSession.GetTimelineProperties();
            if (!ReferenceEquals(changedSession, _session))
                return;

            ResetClockIfTrackChanged(properties.Title, properties.Artist, properties.AlbumTitle);
            _playbackClock.Synchronize(timeline, CurrentState.IsPlaying, timeline.EndTime,
                "media-properties");
            var artwork = await ReadArtworkAsync(properties.Thumbnail);
            if (!ReferenceEquals(changedSession, _session))
                return;

            Publish(CurrentState with
            {
                Title = properties.Title,
                Artist = properties.Artist,
                Album = properties.AlbumTitle,
                ArtworkBytes = artwork,
                Duration = timeline.EndTime > TimeSpan.Zero ? timeline.EndTime : CurrentState.Duration
            });
        }
        catch (Exception)
        {
            // Keep the last good metadata when a player temporarily stops responding.
        }
        finally
        {
            _refreshLock.Release();
        }
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
            Publish(MediaSessionState.Empty);
            return;
        }

        var properties = await session.TryGetMediaPropertiesAsync();
        var playbackInfo = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();
        var isPlaying = playbackInfo.PlaybackStatus ==
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        ResetClockIfTrackChanged(properties.Title, properties.Artist, properties.AlbumTitle);
        _playbackClock.Synchronize(timeline, isPlaying, timeline.EndTime, "session-start");
        var controls = playbackInfo.Controls;
        // The position anchor has already been captured; artwork loading cannot delay it.
        var artwork = await ReadArtworkAsync(properties.Thumbnail);
        if (!ReferenceEquals(session, _session))
            return;

        Publish(new MediaSessionState(
            properties.Title,
            properties.Artist,
            artwork,
            isPlaying,
            controls.IsPlayPauseToggleEnabled ||
                (playbackInfo.PlaybackStatus ==
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                    ? controls.IsPauseEnabled
                    : controls.IsPlayEnabled),
            controls.IsPreviousEnabled,
            controls.IsNextEnabled,
            properties.AlbumTitle,
            timeline.EndTime));
    }

    private static async Task<byte[]?> ReadArtworkAsync(
        IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null)
            return null;

        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            if (stream.Size == 0)
                return null;

            var byteCount = (uint)Math.Min(stream.Size, MaxArtworkBytes);
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            var loadedBytes = await reader.LoadAsync(byteCount);
            if (loadedBytes == 0)
                return null;

            var bytes = new byte[loadedBytes];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Publish(MediaSessionState state)
    {
        CurrentState = state;
        StateChanged?.Invoke(state);
    }

    private void ResetClockIfTrackChanged(string title, string artist, string album)
    {
        var identity = $"{artist}\u001f{title}\u001f{album}";
        if (identity == _clockTrackIdentity)
            return;
        _clockTrackIdentity = identity;
        _playbackClock.Reset("track-changed");
    }

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
    TimeSpan Duration = default)
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
