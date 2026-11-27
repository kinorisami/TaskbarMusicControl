using System.IO;
using System.Text.Json;

namespace TaskbarMusicControls.Services;

/// <summary>
/// Everything the widget remembers about one song, kept in a single
/// <c>&lt;hash&gt;.meta.json</c> next to its cached lyrics.
/// </summary>
internal sealed record LyricsSongState(
    TimeSpan Offset,
    LyricsSource? LockedSource,
    LyricsSource? LastSource,
    DateTimeOffset? FetchedAt)
{
    public static LyricsSongState Empty { get; } = new(
        TimeSpan.Zero, null, null, null);
}

internal static class LyricsSongStateStore
{
    private const int CurrentVersion = 1;

    /// <summary>
    /// How far the offset may be pushed. Wide enough for long intros, small
    /// enough that a runaway adjustment cannot hide the lyrics entirely.
    /// </summary>
    private const int MaxOffsetMs = 15000;

    private static readonly string StateDirectory = LyricsCache.CacheDirectory;

    public static LyricsSongState Load(string trackKey)
    {
        try
        {
            var path = GetPath(trackKey);
            if (!File.Exists(path))
                return LyricsSongState.Empty;

            using var stream = File.OpenRead(path);
            var entry = JsonSerializer.Deserialize<Entry>(stream);
            return entry is { Version: >= CurrentVersion }
                ? new LyricsSongState(
                    TimeSpan.FromMilliseconds(Math.Clamp(entry.OffsetMs, -MaxOffsetMs, MaxOffsetMs)),
                    ParseSource(entry.LockedSource),
                    ParseSource(entry.LastSource),
                    entry.FetchedAt)
                : LyricsSongState.Empty;
        }
        catch (Exception)
        {
            return LyricsSongState.Empty;
        }
    }

    public static void Save(string trackKey, LyricsSongState state)
    {
        try
        {
            Directory.CreateDirectory(StateDirectory);
            var path = GetPath(trackKey);
            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = File.Create(tempPath))
                {
                    JsonSerializer.Serialize(stream, new Entry
                    {
                        Version = CurrentVersion,
                        OffsetMs = Math.Clamp((int)state.Offset.TotalMilliseconds, -MaxOffsetMs, MaxOffsetMs),
                        LockedSource = state.LockedSource?.ToString(),
                        LastSource = state.LastSource?.ToString(),
                        FetchedAt = state.FetchedAt
                    });
                }

                File.Move(tempPath, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                catch (Exception) { }
            }
        }
        catch (Exception)
        {
            // Per-song state is optional: a failed write must not break the widget.
        }
    }

    private static string GetPath(string trackKey) => Path.Combine(
        StateDirectory, LyricsCache.HashTrackKey(trackKey) + ".meta.json");

    private static LyricsSource? ParseSource(string? value) =>
        Enum.TryParse<LyricsSource>(value, out var source) ? source : null;

    private sealed class Entry
    {
        public int Version { get; set; }
        public int OffsetMs { get; set; }
        public string? LockedSource { get; set; }
        public string? LastSource { get; set; }
        public DateTimeOffset? FetchedAt { get; set; }
    }
}
