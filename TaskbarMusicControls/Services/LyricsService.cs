using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TaskbarMusicControls.Services;

internal sealed record LyricsRequest(string Title, string Artist, string Album, TimeSpan Duration);

/// <summary>A place lyrics can come from.</summary>
internal enum LyricsSource { Local, Netease, Lrclib }

/// <summary>Which online provider is asked first when nothing is pinned.</summary>
internal enum LyricsOnlineOrder { NeteaseFirst, LrclibFirst }

/// <summary>
/// The global setting: whether local files win over the online providers, and
/// which of the two online providers comes first.
/// </summary>
internal sealed record LyricsSourceSettings(bool PreferLocal, LyricsOnlineOrder Order)
{
    public static LyricsSourceSettings Default { get; } =
        new(true, LyricsOnlineOrder.NeteaseFirst);
}
internal sealed record LyricTrackLine(TimeSpan? Time, string Text);
internal sealed record LyricFrame(string Text, TimeSpan? Time, TimeSpan? NextTime);
internal sealed record LyricsFrame(LyricFrame? Original, LyricFrame? Translation);
internal sealed class LyricsDocument
{
    private readonly LyricTrackLine[] _originalTimed;
    private readonly LyricTrackLine[] _translationTimed;
    private readonly string? _originalUntimed;
    private readonly string? _translationUntimed;

    public LyricsDocument(IReadOnlyList<LyricTrackLine> originalLines)
        : this(originalLines, Array.Empty<LyricTrackLine>()) { }

    public LyricsDocument(
        IReadOnlyList<LyricTrackLine> originalLines,
        IReadOnlyList<LyricTrackLine> translationLines,
        TimeSpan? duration = null)
    {
        OriginalLines = originalLines;
        TranslationLines = translationLines;
        Duration = duration;
        _originalTimed = BuildTimed(originalLines);
        _translationTimed = BuildTimed(translationLines);
        _originalUntimed = FindUntimed(originalLines);
        _translationUntimed = FindUntimed(translationLines);
    }

    public IReadOnlyList<LyricTrackLine> OriginalLines { get; }
    public IReadOnlyList<LyricTrackLine> TranslationLines { get; }

    /// <summary>
    /// The length of the track the lyrics belong to, when the source reports it.
    /// The player often does not publish a timeline, so this is what the progress
    /// bar falls back to.
    /// </summary>
    public TimeSpan? Duration { get; }

    /// <summary>Which provider produced this document.</summary>
    public LyricsSource? Source { get; init; }

    public LyricsDocument WithDuration(TimeSpan? duration) =>
        new(OriginalLines, TranslationLines, duration) { Source = Source };

    public LyricsDocument WithSource(LyricsSource source) =>
        new(OriginalLines, TranslationLines, Duration) { Source = source };

    public bool HasLyrics => OriginalLines.Count > 0;
    public bool IsTimed => _originalTimed.Length > 0;

    public LyricsFrame? At(TimeSpan position)
    {
        if (!HasLyrics) return null;
        return new LyricsFrame(
            AtTrack(_originalTimed, _originalUntimed, position),
            AtTrack(_translationTimed, _translationUntimed, position));
    }

    private static LyricFrame? AtTrack(
        LyricTrackLine[] timed,
        string? untimedText,
        TimeSpan position)
    {
        if (timed.Length > 0)
        {
            var index = FindLastIndexAtOrBefore(timed, position);
            // Wait for the first timestamp instead of scrolling an upcoming lyric during the intro.
            if (index < 0) return null;

            var current = timed[index];
            TimeSpan? nextTime = index + 1 < timed.Length
                ? timed[index + 1].Time
                : null;
            return new LyricFrame(current.Text, current.Time, nextTime);
        }

        // Untimed-only tracks show their plain text; they have no sync timeline.
        return untimedText is null
            ? null
            : new LyricFrame(untimedText, null, null);
    }

    private static int FindLastIndexAtOrBefore(LyricTrackLine[] lines, TimeSpan position)
    {
        var lo = 0;
        var hi = lines.Length - 1;
        var result = -1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (lines[mid].Time!.Value <= position)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return result;
    }

    private static LyricTrackLine[] BuildTimed(IReadOnlyList<LyricTrackLine> lines) =>
        lines.Where(line => line.Time.HasValue)
            .OrderBy(line => line.Time!.Value)
            .ToArray();

    private static string? FindUntimed(IReadOnlyList<LyricTrackLine> lines) =>
        lines.FirstOrDefault(line =>
            !line.Time.HasValue && !string.IsNullOrWhiteSpace(line.Text))?.Text;
}

internal interface ILyricsProvider
{
    Task<LyricsDocument?> FindAsync(LyricsRequest request, CancellationToken cancellationToken);
}

internal sealed class LyricsService
{
    private readonly LocalLyricsProvider _local = new();
    private readonly NeteaseLyricsProvider _netease = new();
    private readonly LrclibLyricsProvider _online = new();
    private readonly LyricsCache _cache = new();

    /// <summary>
    /// Looks the song up again while ignoring what is cached, so a wrong match
    /// (a cover, a live version, an instrumental) - or a source changed by the
    /// user - can be replaced. Nothing is deleted: the fresh answer is written
    /// over its own cache entry, and when the lookup finds nothing the cached
    /// lyrics are used as they are.
    /// </summary>
    public async Task<LyricsDocument?> RefetchAsync(
        LyricsRequest request, LyricsSourceSettings settings, LyricsSource? locked, CancellationToken token)
    {
        var fresh = await FindAsync(request, settings, locked, token, bypassCache: true)
            .ConfigureAwait(false);
        if (fresh is { HasLyrics: true })
            return fresh;

        // Nothing better was found, so the existing cache still decides.
        return await FindAsync(request, settings, locked, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Looks the song up in two stages. Everything already on disk is tried
    /// first - the local files (when preferred) and then the caches of both
    /// online providers in priority order - and only when none of them has the
    /// lyrics are the providers asked, in the same order. A pinned song skips
    /// all of that and only asks its source. With <paramref name="bypassCache"/>
    /// the caches are skipped for this call but left untouched on disk.
    /// </summary>
    public async Task<LyricsDocument?> FindAsync(
        LyricsRequest request, LyricsSourceSettings settings, LyricsSource? locked,
        CancellationToken token, bool bypassCache = false)
    {
        if (locked is { } pinned)
            return await FindInSourceAsync(request, pinned, bypassCache, token).ConfigureAwait(false);

        var order = settings.Order == LyricsOnlineOrder.NeteaseFirst
            ? new[] { LyricsSource.Netease, LyricsSource.Lrclib }
            : new[] { LyricsSource.Lrclib, LyricsSource.Netease };

        if (settings.PreferLocal)
            return await FindOfflineFirstAsync(request, order, bypassCache, token).ConfigureAwait(false);

        return await FindBySourcePriorityAsync(request, order, bypassCache, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Local files first: the local files, then both caches, and only when none
    /// of them has the lyrics are the providers asked.
    /// </summary>
    private async Task<LyricsDocument?> FindOfflineFirstAsync(
        LyricsRequest request, LyricsSource[] order, bool bypassCache, CancellationToken token)
    {
        var local = await FetchAsync(request, LyricsSource.Local, token).ConfigureAwait(false);
        if (local is { HasLyrics: true }) return local;
        token.ThrowIfCancellationRequested();

        if (!bypassCache)
        {
            foreach (var source in order)
            {
                var cached = await ReadCachedAsync(request, source, token).ConfigureAwait(false);
                if (cached is { HasLyrics: true }) return cached;
                token.ThrowIfCancellationRequested();
            }
        }

        foreach (var source in order)
        {
            var fetched = await FetchAsync(request, source, token).ConfigureAwait(false);
            if (fetched is { HasLyrics: true })
            {
                await TryCacheAsync(request, SourceKey(source), fetched, token).ConfigureAwait(false);
                return fetched;
            }

            token.ThrowIfCancellationRequested();
        }

        return null;
    }

    /// <summary>
    /// Source priority: one source is finished - its cache and then its network
    /// call - before the next source is asked.
    /// </summary>
    private async Task<LyricsDocument?> FindBySourcePriorityAsync(
        LyricsRequest request, LyricsSource[] order, bool bypassCache, CancellationToken token)
    {
        foreach (var source in order)
        {
            if (!bypassCache)
            {
                var cached = await ReadCachedAsync(request, source, token).ConfigureAwait(false);
                if (cached is { HasLyrics: true }) return cached;
                token.ThrowIfCancellationRequested();
            }

            var fetched = await FetchAsync(request, source, token).ConfigureAwait(false);
            if (fetched is { HasLyrics: true })
            {
                await TryCacheAsync(request, SourceKey(source), fetched, token).ConfigureAwait(false);
                return fetched;
            }

            token.ThrowIfCancellationRequested();
        }

        return null;
    }

    /// <summary>
    /// A pinned song only asks its source: cached lyrics first, the provider
    /// second.
    /// </summary>
    private async Task<LyricsDocument?> FindInSourceAsync(
        LyricsRequest request, LyricsSource source, bool bypassCache, CancellationToken token)
    {
        if (!bypassCache)
        {
            var cached = await ReadCachedAsync(request, source, token).ConfigureAwait(false);
            if (cached is { HasLyrics: true }) return cached;
        }

        var fetched = await FetchAsync(request, source, token).ConfigureAwait(false);
        if (fetched is not { HasLyrics: true }) return fetched;

        if (source != LyricsSource.Local)
            await TryCacheAsync(request, SourceKey(source), fetched, token).ConfigureAwait(false);
        return fetched;
    }

    /// <summary>Reads the cache of one source. Errors mean "not cached".</summary>
    private async Task<LyricsDocument?> ReadCachedAsync(
        LyricsRequest request, LyricsSource source, CancellationToken token)
    {
        if (source == LyricsSource.Local)
            return null;

        try
        {
            return await _cache.ReadAsync(request, SourceKey(source), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>Asks one provider. Errors mean "not found".</summary>
    private async Task<LyricsDocument?> FetchAsync(
        LyricsRequest request, LyricsSource source, CancellationToken token)
    {
        try
        {
            return source switch
            {
                LyricsSource.Local => await _local.FindAsync(request, token).ConfigureAwait(false),
                LyricsSource.Netease => await _netease.FindAsync(request, token).ConfigureAwait(false),
                _ => await _online.FindAsync(request, token).ConfigureAwait(false)
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>The name a source is cached and stored under.</summary>
    internal static string SourceKey(LyricsSource source) => source switch
    {
        LyricsSource.Netease => "netease",
        LyricsSource.Lrclib => "lrclib",
        _ => "local"
    };

    private async Task TryCacheAsync(
        LyricsRequest request, string source, LyricsDocument lyrics, CancellationToken token)
    {
        try { await _cache.WriteAsync(request, source, lyrics, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { /* Keep fetched lyrics if the disk cache cannot be written. */ }
    }
}

internal sealed class LyricsCache
{
    internal static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskbarMusicControls", "Lyrics", "Cache");

    /// <summary>
    /// The file name a song is cached under. Accepts the "artist␟title␟album"
    /// key the widget service already uses, so the cache, the per-song state and
    /// the diagnostics all agree on one hash.
    /// </summary>
    internal static string HashTrackKey(string trackKey)
    {
        var parts = trackKey.Split('\u001f');
        return Hash(string.Join("\u001f",
            (parts.Length > 0 ? parts[0] : "").Trim(),
            (parts.Length > 1 ? parts[1] : "").Trim(),
            (parts.Length > 2 ? parts[2] : "").Trim()));
    }

    private static string Hash(string identity)
    {
        var normalized = identity.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    public Task<LyricsDocument?> ReadAsync(LyricsRequest request, string source, CancellationToken token) =>
        ReadPathAsync(GetPath(request, source), token);

    private static LyricsSource? ParseSource(string? value) => value switch
    {
        "netease" => LyricsSource.Netease,
        "lrclib" => LyricsSource.Lrclib,
        "local" => LyricsSource.Local,
        _ => null
    };

    private static async Task<LyricsDocument?> ReadPathAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            var entry = await JsonSerializer.DeserializeAsync<LyricsCacheEntry>(
                stream, cancellationToken: token).ConfigureAwait(false);
            if (entry is null) return null;
            // Entries written before the duration was stored are treated as a
            // miss so they are fetched again and rewritten with the new field.
            if (entry.Version < LyricsCacheEntry.CurrentVersion) return null;
            if (entry.OriginalLines is { Length: > 0 } originalLines)
            {
                var document = new LyricsDocument(originalLines, entry.TranslationLines ?? [], entry.Duration);
                return ParseSource(entry.Source) is { } source ? document.WithSource(source) : document;
            }
            if (entry.Lines is { Length: > 0 } legacyLines)
            {
                var original = legacyLines.Select(line => new LyricTrackLine(line.Time, line.Original)).ToArray();
                var translation = legacyLines.Where(line => !string.IsNullOrWhiteSpace(line.Translation))
                    .Select(line => new LyricTrackLine(line.Time, line.Translation!)).ToArray();
                var document = new LyricsDocument(original, translation, entry.Duration);
                return ParseSource(entry.Source) is { } source ? document.WithSource(source) : document;
            }
            return null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (JsonException)
        {
            // The file is there but its content is broken: drop it so the next
            // lookup can fetch the lyrics again.
            TryDelete(path);
            return null;
        }
        catch (NotSupportedException)
        {
            TryDelete(path);
            return null;
        }
        catch (Exception)
        {
            // A transient failure (file locked, antivirus scan, disk hiccup)
            // must not throw away a perfectly valid cache entry.
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception) { }
    }

    public async Task WriteAsync(
        LyricsRequest request, string source, LyricsDocument lyrics, CancellationToken token)
    {
        Directory.CreateDirectory(CacheDirectory);
        var path = GetPath(request, source);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream, new LyricsCacheEntry(
                        lyrics.OriginalLines.ToArray(), lyrics.TranslationLines.ToArray(),
                        Duration: lyrics.Duration,
                        Version: LyricsCacheEntry.CurrentVersion,
                        Source: lyrics.Source is { } documentSource
                            ? LyricsService.SourceKey(documentSource)
                            : null),
                    cancellationToken: token).ConfigureAwait(false);
            }
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch (Exception) { }
        }
    }

    private static string GetPath(LyricsRequest request, string source) =>
        Path.Combine(CacheDirectory, GetHash(request) + "." + source + ".json");

    private static string GetHash(LyricsRequest request) =>
        Hash(string.Join("\u001f", request.Artist.Trim(), request.Title.Trim(), request.Album.Trim()));

    private sealed record LyricsCacheEntry(
        LyricTrackLine[]? OriginalLines = null,
        LyricTrackLine[]? TranslationLines = null,
        LegacyLyricLine[]? Lines = null,
        TimeSpan? Duration = null,
        int Version = 0,
        string? Source = null)
    {
        /// <summary>
        /// Bumped whenever the stored shape changes. Entries of an older version
        /// are ignored and fetched again.
        /// </summary>
        public const int CurrentVersion = 2;
    }
    private sealed record LegacyLyricLine(TimeSpan? Time, string Original, string? Translation = null);
}

internal sealed class LocalLyricsProvider : ILyricsProvider
{
    private static readonly string LyricsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskbarMusicControls", "Lyrics");

    public async Task<LyricsDocument?> FindAsync(LyricsRequest request, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(LyricsDirectory);
        var names = new[]
        {
            $"{Safe(request.Artist)} - {Safe(request.Title)}.lrc",
            $"{Safe(request.Title)}.lrc",
            $"{Safe(request.Artist)} - {Safe(request.Title)}.txt",
            $"{Safe(request.Title)}.txt"
        }.Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(LyricsDirectory, name);
            if (!File.Exists(path)) continue;
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var parsed = LyricsParser.Parse(text);
            if (parsed.HasLyrics) return parsed.WithSource(LyricsSource.Local);
        }
        return null;
    }

    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(ch => invalid.Contains(ch) ? '_' : ch)).Trim();
    }
}

internal sealed class NeteaseLyricsProvider : ILyricsProvider
{
    private static readonly HttpClient Client = CreateClient();
    private static readonly Uri SearchEndpoint = new("https://music.163.com/api/search/get");
    private static readonly Uri LyricsEndpoint = new("https://music.163.com/api/song/lyric");

    public async Task<LyricsDocument?> FindAsync(LyricsRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Artist))
            return null;

        var keywords = request.Artist + " " + request.Title;
        var searchUri = AddQuery(SearchEndpoint, new Dictionary<string, string>
        {
            ["s"] = keywords,
            ["type"] = "1",
            ["limit"] = "10",
            ["offset"] = "0"
        });
        using var searchResponse = await Client.GetAsync(searchUri, cancellationToken).ConfigureAwait(false);
        if (!searchResponse.IsSuccessStatusCode) return null;
        await using var searchStream = await searchResponse.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var searchJson = await JsonDocument.ParseAsync(
            searchStream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!searchJson.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array)
            return null;

        var requestedTitle = NormalizeMatch(request.Title);
        var requestedArtist = NormalizeMatch(request.Artist);
        JsonElement? bestSong = null;
        var bestScore = 0;
        foreach (var song in songs.EnumerateArray())
        {
            var name = GetString(song, "name");
            var title = NormalizeMatch(name);
            if (title.Length == 0 || !(title == requestedTitle || title.Contains(requestedTitle) || requestedTitle.Contains(title)))
                continue;

            var artists = GetArtistNames(song);
            var normalizedArtists = artists.Select(NormalizeMatch).ToArray();
            var artistMatch = normalizedArtists.Any(artist =>
                artist == requestedArtist || artist.Contains(requestedArtist) || requestedArtist.Contains(artist));
            if (!artistMatch) continue;

            var score = title == requestedTitle ? 4 : 2;
            if (normalizedArtists.Contains(requestedArtist, StringComparer.Ordinal)) score += 2;
            var album = GetAlbumName(song);
            if (!string.IsNullOrWhiteSpace(request.Album) &&
                NormalizeMatch(album) == NormalizeMatch(request.Album)) score++;
            if (score <= bestScore) continue;
            bestScore = score;
            bestSong = song;
        }

        if (bestSong is not { } match || !match.TryGetProperty("id", out var id)) return null;
        var songId = id.ValueKind == JsonValueKind.String ? id.GetString() : id.ToString();
        if (string.IsNullOrWhiteSpace(songId)) return null;

        var lyricsUri = AddQuery(LyricsEndpoint, new Dictionary<string, string>
        {
            ["id"] = songId,
            ["lv"] = "-1",
            ["tv"] = "-1"
        });
        using var lyricsResponse = await Client.GetAsync(lyricsUri, cancellationToken).ConfigureAwait(false);
        if (!lyricsResponse.IsSuccessStatusCode) return null;
        await using var lyricsStream = await lyricsResponse.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var lyricsJson = await JsonDocument.ParseAsync(
            lyricsStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = lyricsJson.RootElement;
        var originalText = GetNestedString(root, "lrc", "lyric");
        if (string.IsNullOrWhiteSpace(originalText)) return null;
        // The matched song carries its length in the search result, so the
        // progress bar gets a duration even when the player publishes none.
        var duration = GetSongDuration(match);
        var original = LyricsParser.Parse(originalText);
        var translationText = GetNestedString(root, "tlyric", "lyric");
        if (string.IsNullOrWhiteSpace(translationText))
            return original.WithDuration(duration).WithSource(LyricsSource.Netease);
        var translation = LyricsParser.Parse(translationText);
        return LyricsParser.AddTranslation(original, translation)
            .WithDuration(duration)
            .WithSource(LyricsSource.Netease);
    }

    /// <summary>
    /// Reads the track length from a search result. `/api/search/get` returns it
    /// as `duration` in milliseconds, `/api/cloudsearch/pc` as `dt`.
    /// </summary>
    private static TimeSpan? GetSongDuration(JsonElement song)
    {
        foreach (var name in new[] { "duration", "dt" })
        {
            if (song.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt64(out var milliseconds) &&
                milliseconds > 0)
                return TimeSpan.FromMilliseconds(milliseconds);
        }

        return null;
    }

    private static string[] GetArtistNames(JsonElement song)
    {
        var propertyName = song.TryGetProperty("artists", out var artists) ? "artists" : "ar";
        if (!song.TryGetProperty(propertyName, out artists) || artists.ValueKind != JsonValueKind.Array)
            return [];
        return artists.EnumerateArray().Select(artist => GetString(artist, "name"))
            .Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
    }

    private static string GetAlbumName(JsonElement song)
    {
        var propertyName = song.TryGetProperty("album", out _) ? "album" : "al";
        return song.TryGetProperty(propertyName, out var album) ? GetString(album, "name") : "";
    }

    private static string GetNestedString(JsonElement root, string objectName, string propertyName) =>
        root.TryGetProperty(objectName, out var item) ? GetString(item, propertyName) : "";

    private static string GetString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string NormalizeMatch(string text) =>
        string.Concat(text.Normalize(NormalizationForm.FormKC)
            .ToUpperInvariant().Where(char.IsLetterOrDigit));

    private static Uri AddQuery(Uri endpoint, IReadOnlyDictionary<string, string> values) =>
        new UriBuilder(endpoint)
        {
            Query = string.Join("&", values.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))
        }.Uri;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36");
        client.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
        return client;
    }
}

internal sealed class LrclibLyricsProvider : ILyricsProvider
{
    private static readonly HttpClient Client = CreateClient();
    private static readonly Uri Endpoint = new("https://lrclib.net/api/get");

    public async Task<LyricsDocument?> FindAsync(LyricsRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Artist)) return null;
        var query = new Dictionary<string, string>
        {
            ["track_name"] = request.Title,
            ["artist_name"] = request.Artist
        };
        if (!string.IsNullOrWhiteSpace(request.Album)) query["album_name"] = request.Album;
        if (request.Duration > TimeSpan.Zero) query["duration"] = Math.Round(request.Duration.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        var builder = new UriBuilder(Endpoint) { Query = string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}")) };
        using var response = await Client.GetAsync(builder.Uri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        var duration = GetDuration(root);
        if (root.TryGetProperty("syncedLyrics", out var synced) && synced.ValueKind == JsonValueKind.String)
        {
            var parsed = LyricsParser.Parse(synced.GetString() ?? "");
            if (parsed.HasLyrics)
                return parsed.WithDuration(duration).WithSource(LyricsSource.Lrclib);
        }
        if (root.TryGetProperty("plainLyrics", out var plain) && plain.ValueKind == JsonValueKind.String)
            return LyricsParser.Parse(plain.GetString() ?? "")
                .WithDuration(duration)
                .WithSource(LyricsSource.Lrclib);
        return null;
    }

    /// <summary>LRCLIB returns the track length in seconds.</summary>
    private static TimeSpan? GetDuration(JsonElement root) =>
        root.TryGetProperty("duration", out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var seconds) &&
        seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TaskbarMusicControls/1.0");
        return client;
    }
}

internal static partial class LyricsParser
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{2})(?:\.(\d{1,3}))?\]")]
    private static partial Regex TimestampRegex();

    public static LyricsDocument Parse(string text)
    {
        var timed = new List<(TimeSpan Time, string Text)>();
        var untimed = new List<string>();
        foreach (var raw in text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
        {
            var line = raw.Trim();
            if (line.Length == 0 || (line.StartsWith('[') && !TimestampRegex().IsMatch(line))) continue;
            var matches = TimestampRegex().Matches(line);
            var lyric = TimestampRegex().Replace(line, "").Trim();
            if (lyric.Length == 0) continue;
            if (matches.Count == 0)
            {
                untimed.Add(lyric);
                continue;
            }
            foreach (Match match in matches)
            {
                var fraction = match.Groups[3].Value;
                var milliseconds = fraction.Length switch
                {
                    1 => int.Parse(fraction, CultureInfo.InvariantCulture) * 100,
                    2 => int.Parse(fraction, CultureInfo.InvariantCulture) * 10,
                    3 => int.Parse(fraction, CultureInfo.InvariantCulture),
                    _ => 0
                };
                var time = TimeSpan.FromMinutes(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)) +
                           TimeSpan.FromSeconds(int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)) +
                           TimeSpan.FromMilliseconds(milliseconds);
                timed.Add((time, lyric));
            }
        }

        if (timed.Count == 0)
        {
            if (untimed.Count == 0)
                return new LyricsDocument(Array.Empty<LyricTrackLine>());
            return new LyricsDocument(new[]
            {
                new LyricTrackLine(null, string.Join("　·　", untimed))
            });
        }

        var ordered = timed.OrderBy(entry => entry.Time).ToList();
        var originals = new List<LyricTrackLine>();
        var translations = new List<LyricTrackLine>();
        foreach (var group in ordered.GroupBy(entry => entry.Time))
        {
            var entries = group.ToArray();
            originals.Add(new LyricTrackLine(group.Key, entries[0].Text));
            if (entries.Length > 1)
                translations.Add(new LyricTrackLine(group.Key, entries[1].Text));
        }
        return new LyricsDocument(originals, translations);
    }

    public static LyricsDocument AddTranslation(LyricsDocument original, LyricsDocument translation)
    {
        if (!original.HasLyrics || translation.OriginalLines.Count == 0) return original;
        return new LyricsDocument(original.OriginalLines, translation.OriginalLines);
    }
}
