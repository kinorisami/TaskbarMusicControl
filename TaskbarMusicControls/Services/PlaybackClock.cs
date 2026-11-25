using System.Diagnostics;
using System.IO;
using Windows.Media.Control;

namespace TaskbarMusicControls.Services;

/// <summary>
/// Keeps one monotonic playback clock and uses GSMTC timeline snapshots only as corrections.
/// </summary>
internal sealed class PlaybackClock
{
    private static readonly TimeSpan PauseSnapshotFreshness = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan UnknownDurationMaxAge = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FutureTimestampTolerance = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SeekThreshold = TimeSpan.FromMilliseconds(1250);
    private readonly object _gate = new();
    private TimeSpan _anchorPosition;
    private TimeSpan _duration;
    private TimeSpan _lastAcceptedRawPosition;
    private long _anchorTimestamp;
    private DateTimeOffset _lastAcceptedTimelineTime;
    private bool _isPlaying;
    private bool _hasAnchor;

    public TimeSpan GetCurrentPosition()
    {
        lock (_gate)
            return GetCurrentPositionLocked(Stopwatch.GetTimestamp());
    }

    public void Reset(string reason)
    {
        lock (_gate)
        {
            _anchorPosition = TimeSpan.Zero;
            _duration = TimeSpan.Zero;
            _anchorTimestamp = Stopwatch.GetTimestamp();
            _lastAcceptedTimelineTime = DateTimeOffset.MinValue;
            _lastAcceptedRawPosition = TimeSpan.Zero;
            _isPlaying = false;
            _hasAnchor = false;
        }
        PlaybackSyncDiagnostics.Write($"reset reason={reason}");
    }

    public void Synchronize(
        GlobalSystemMediaTransportControlsSessionTimelineProperties timeline,
        bool isPlaying,
        TimeSpan duration,
        string reason)
    {
        var receivedUtc = DateTimeOffset.UtcNow;
        var receivedTimestamp = Stopwatch.GetTimestamp();
        var rawPosition = timeline.Position;
        var updatedUtc = timeline.LastUpdatedTime.ToUniversalTime();
        var age = updatedUtc == DateTimeOffset.MinValue
            ? (TimeSpan?)null
            : receivedUtc - updatedUtc;

        lock (_gate)
        {
            _duration = duration > TimeSpan.Zero ? duration : timeline.EndTime;
            var previousPlaying = _isPlaying;
            var current = GetCurrentPositionLocked(receivedTimestamp);
            var candidateValid = TryGetCurrentSnapshotPosition(
                rawPosition, _duration, age, isPlaying, out var candidate, out var rejection);
            if (candidateValid && _lastAcceptedTimelineTime != DateTimeOffset.MinValue &&
                updatedUtc < _lastAcceptedTimelineTime - TimeSpan.FromMilliseconds(100))
            {
                candidateValid = false;
                rejection = "out-of-order-timestamp";
            }
            if (candidateValid && isPlaying && previousPlaying &&
                _lastAcceptedTimelineTime != DateTimeOffset.MinValue &&
                updatedUtc - _lastAcceptedTimelineTime >= TimeSpan.FromMilliseconds(750) &&
                (rawPosition - _lastAcceptedRawPosition).Duration() <= TimeSpan.FromMilliseconds(100))
            {
                candidateValid = false;
                rejection = "timestamp-advances-position-stalled";
            }

            if (!isPlaying)
            {
                // A fresh paused snapshot is authoritative. If it is stale, freeze our
                // own estimate at receipt time instead of snapping back to stale media data.
                if (candidateValid && age is { } pausedAge && pausedAge <= PauseSnapshotFreshness)
                    SetAnchor(candidate, false, receivedTimestamp);
                else if (previousPlaying && _hasAnchor)
                    SetAnchor(current, false, receivedTimestamp);
                else if (!_hasAnchor && IsPlausibleRawPosition(rawPosition, _duration))
                    SetAnchor(rawPosition, false, receivedTimestamp);
                else
                    SetAnchor(current, false, receivedTimestamp);
            }
            else if (candidateValid)
            {
                if (!_hasAnchor || !previousPlaying)
                {
                    SetAnchor(candidate, true, receivedTimestamp);
                }
                else
                {
                    var difference = candidate - current;
                    if (difference.Duration() >= SeekThreshold)
                    {
                        SetAnchor(candidate, true, receivedTimestamp);
                        PlaybackSyncDiagnostics.Write(
                            $"resync reason={reason} kind=discontinuity rawMs={rawPosition.TotalMilliseconds:F0} " +
                            $"ageMs={age?.TotalMilliseconds:F0} predictedMs={current.TotalMilliseconds:F0} " +
                            $"sampleMs={candidate.TotalMilliseconds:F0} deltaMs={difference.TotalMilliseconds:F0}");
                    }
                    else
                    {
                        // Small drift is corrected without allowing noisy samples to move
                        // the displayed lyric line back and forth.
                        SetAnchor(current + TimeSpan.FromTicks(difference.Ticks / 2), true, receivedTimestamp);
                    }
                }
            }
            else
            {
                if (!_hasAnchor)
                    SetAnchor(IsPlausibleRawPosition(rawPosition, _duration) ? rawPosition : TimeSpan.Zero,
                        isPlaying, receivedTimestamp);
                else if (!previousPlaying && isPlaying)
                    SetAnchor(current, true, receivedTimestamp);

                PlaybackSyncDiagnostics.Write(
                    $"sample-rejected reason={reason} status={(isPlaying ? "playing" : "paused")} " +
                    $"rawMs={rawPosition.TotalMilliseconds:F0} ageMs={age?.TotalMilliseconds:F0} " +
                    $"durationMs={_duration.TotalMilliseconds:F0} detail={rejection}");
            }

            if (candidateValid)
            {
                _lastAcceptedTimelineTime = updatedUtc;
                _lastAcceptedRawPosition = rawPosition;
            }
            _isPlaying = isPlaying;
        }
    }

    private bool TryGetCurrentSnapshotPosition(
        TimeSpan rawPosition,
        TimeSpan duration,
        TimeSpan? age,
        bool isPlaying,
        out TimeSpan position,
        out string rejection)
    {
        position = rawPosition;
        rejection = "";
        if (!IsPlausibleRawPosition(rawPosition, duration))
        {
            rejection = "invalid-position";
            return false;
        }

        if (age is not { } snapshotAge)
        {
            rejection = "missing-timestamp";
            return false;
        }
        if (snapshotAge < -FutureTimestampTolerance)
        {
            rejection = "future-timestamp";
            return false;
        }
        if (updatedTimestampTooOld(snapshotAge, duration))
        {
            rejection = "stale-timestamp";
            return false;
        }

        if (isPlaying && snapshotAge > TimeSpan.Zero)
            position += snapshotAge;

        if (duration > TimeSpan.Zero && position > duration + TimeSpan.FromSeconds(2))
        {
            rejection = "extrapolation-past-duration";
            return false;
        }
        if (duration > TimeSpan.Zero && position > duration)
            position = duration;
        return true;
    }

    private static bool updatedTimestampTooOld(TimeSpan age, TimeSpan duration) =>
        duration > TimeSpan.Zero
            ? age > duration + TimeSpan.FromSeconds(5)
            : age > UnknownDurationMaxAge;

    private static bool IsPlausibleRawPosition(TimeSpan position, TimeSpan duration) =>
        position >= TimeSpan.Zero &&
        (duration <= TimeSpan.Zero || position <= duration + TimeSpan.FromSeconds(2));

    private TimeSpan GetCurrentPositionLocked(long now)
    {
        var position = _anchorPosition;
        if (_isPlaying && _anchorTimestamp != 0)
            position += Stopwatch.GetElapsedTime(_anchorTimestamp, now);
        if (position < TimeSpan.Zero)
            return TimeSpan.Zero;
        return _duration > TimeSpan.Zero && position > _duration ? _duration : position;
    }

    private void SetAnchor(TimeSpan position, bool isPlaying, long timestamp)
    {
        _anchorPosition = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        if (_duration > TimeSpan.Zero && _anchorPosition > _duration)
            _anchorPosition = _duration;
        _anchorTimestamp = timestamp;
        _isPlaying = isPlaying;
        _hasAnchor = true;
    }
}

internal static class PlaybackSyncDiagnostics
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskbarMusicControls", "Logs", "playback-sync.log");
    private const long MaxLogBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length >= MaxLogBytes)
                {
                    var previous = LogPath + ".1";
                    if (File.Exists(previous)) File.Delete(previous);
                    File.Move(LogPath, previous);
                }

                File.AppendAllText(LogPath,
                    $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Diagnostics must never interfere with media playback or UI updates.
        }
    }
}
