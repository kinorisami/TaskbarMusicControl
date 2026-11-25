using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TaskbarMusicControls.Services;

internal sealed record LyricsRequest(string Title, string Artist, string Album, TimeSpan Duration);
internal sealed record LyricTrackLine(TimeSpan? Time, string Text);
internal sealed record LyricFrame(string Text, TimeSpan? Time, TimeSpan? NextTime);
internal sealed record LyricsFrame(LyricFrame? Original, LyricFrame? Translation);
internal sealed record LyricsDocument(
    IReadOnlyList<LyricTrackLine> OriginalLines,
    IReadOnlyList<LyricTrackLine> TranslationLines)
{
    public LyricsDocument(IReadOnlyList<LyricTrackLine> originalLines)
        : this(originalLines, Array.Empty<LyricTrackLine>()) { }

    public bool HasLyrics => OriginalLines.Count > 0;
    public bool IsTimed => OriginalLines.Any(line => line.Time.HasValue);

    public LyricsFrame? At(TimeSpan position)
    {
        if (!HasLyrics) return null;
        return new LyricsFrame(AtTrack(OriginalLines, position),
            AtTrack(TranslationLines, position));
    }

    private static LyricFrame? AtTrack(IReadOnlyList<LyricTrackLine> lines, TimeSpan position)
    {
        if (lines.Count == 0) return null;
        var index = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].Time.HasValue) { index = i; break; }
            if (lines[i].Time!.Value <= position) index = i;
            else break;
        }
        // Wait for the first timestamp instead of scrolling an upcoming lyric during the intro.
        if (index < 0) return null;
        var line = lines[index];
        TimeSpan? nextTime = null;
        for (var next = index + 1; next < lines.Count; next++)
        {
            if (!lines[next].Time.HasValue) continue;
            nextTime = lines[next].Time;
            break;
        }
        return new LyricFrame(line.Text, line.Time, nextTime);
    }
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

    public async Task<LyricsDocument?> FindAsync(LyricsRequest request, CancellationToken token)
    {
        try
        {
            var local = await _local.FindAsync(request, token).ConfigureAwait(false);
        if (local is { HasLyrics: true }) return local;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { /* Continue with the cache and online provider. */ }

        token.ThrowIfCancellationRequested();
        var neteaseCached = await _cache.ReadAsync(request, "netease", token).ConfigureAwait(false);
        if (neteaseCached is { HasLyrics: true }) return neteaseCached;

        try
        {
            var neteaseLyrics = await _netease.FindAsync(request, token).ConfigureAwait(false);
            if (neteaseLyrics is { HasLyrics: true })
            {
                await TryCacheAsync(request, "netease", neteaseLyrics, token).ConfigureAwait(false);
                return neteaseLyrics;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { /* Continue to the LRCLIB fallback. */ }

        var cached = await _cache.ReadAsync(request, "lrclib", token).ConfigureAwait(false);
        if (cached is { HasLyrics: true }) return cached;

        // Read caches created by older versions, which did not record the provider.
        cached = await _cache.ReadLegacyAsync(request, token).ConfigureAwait(false);
        if (cached is { HasLyrics: true }) return cached;

        try
        {
            var online = await _online.FindAsync(request, token).ConfigureAwait(false);
            if (online is not { HasLyrics: true }) return null;
            await TryCacheAsync(request, "lrclib", online, token).ConfigureAwait(false);
            return online;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

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
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskbarMusicControls", "Lyrics", "Cache");

    public Task<LyricsDocument?> ReadAsync(LyricsRequest request, string source, CancellationToken token) =>
        ReadPathAsync(GetPath(request, source), token);

    public Task<LyricsDocument?> ReadLegacyAsync(LyricsRequest request, CancellationToken token) =>
        ReadPathAsync(Path.Combine(CacheDirectory, GetHash(request) + ".json"), token);

    private static async Task<LyricsDocument?> ReadPathAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            var entry = await JsonSerializer.DeserializeAsync<LyricsCacheEntry>(
                stream, cancellationToken: token).ConfigureAwait(false);
            if (entry is null) return null;
            if (entry.OriginalLines is { Length: > 0 } originalLines)
                return new LyricsDocument(originalLines, entry.TranslationLines ?? []);
            if (entry.Lines is { Length: > 0 } legacyLines)
            {
                var original = legacyLines.Select(line => new LyricTrackLine(line.Time, line.Original)).ToArray();
                var translation = legacyLines.Where(line => !string.IsNullOrWhiteSpace(line.Translation))
                    .Select(line => new LyricTrackLine(line.Time, line.Translation!)).ToArray();
                return new LyricsDocument(original, translation);
            }
            return null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            try { File.Delete(path); } catch (Exception) { }
            return null;
        }
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
                        lyrics.OriginalLines.ToArray(), lyrics.TranslationLines.ToArray()), cancellationToken: token)
                    .ConfigureAwait(false);
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

    private static string GetHash(LyricsRequest request)
    {
        var identity = string.Join("\u001f", request.Artist.Trim(), request.Title.Trim(), request.Album.Trim())
            .Normalize(NormalizationForm.FormKC)
            .ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return hash;
    }

    private sealed record LyricsCacheEntry(
        LyricTrackLine[]? OriginalLines = null,
        LyricTrackLine[]? TranslationLines = null,
        LegacyLyricLine[]? Lines = null);
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
            if (parsed.HasLyrics) return parsed;
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
        var original = LyricsParser.Parse(originalText);
        var translationText = GetNestedString(root, "tlyric", "lyric");
        if (string.IsNullOrWhiteSpace(translationText)) return original;
        var translation = LyricsParser.Parse(translationText);
        return LyricsParser.AddTranslation(original, translation);
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
        if (root.TryGetProperty("syncedLyrics", out var synced) && synced.ValueKind == JsonValueKind.String)
        {
            var parsed = LyricsParser.Parse(synced.GetString() ?? "");
            if (parsed.HasLyrics) return parsed;
        }
        if (root.TryGetProperty("plainLyrics", out var plain) && plain.ValueKind == JsonValueKind.String)
            return LyricsParser.Parse(plain.GetString() ?? "");
        return null;
    }

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
        var parsed = new List<(TimeSpan? Time, string Text)>();
        foreach (var raw in text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
        {
            var line = raw.Trim();
            if (line.Length == 0 || (line.StartsWith('[') && !TimestampRegex().IsMatch(line))) continue;
            var matches = TimestampRegex().Matches(line);
            var lyric = TimestampRegex().Replace(line, "").Trim();
            if (lyric.Length == 0) continue;
            if (matches.Count == 0) parsed.Add((null, lyric));
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
                parsed.Add((time, lyric));
            }
        }

        if (parsed.Count > 0 && parsed.All(entry => !entry.Time.HasValue))
            return new LyricsDocument(new[]
            {
                new LyricTrackLine(null, string.Join("　·　", parsed.Select(entry => entry.Text)))
            });

        parsed = parsed.OrderBy(entry => entry.Time).ToList();
        var originals = new List<LyricTrackLine>();
        var translations = new List<LyricTrackLine>();
        foreach (var group in parsed.GroupBy(entry => entry.Time))
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
