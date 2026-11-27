using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskbarMusicControls.Overlay;

namespace TaskbarMusicControls.Services;

/// <summary>Everything the widget remembers between runs, except per-song state.</summary>
internal sealed record WidgetSettings(
    WidgetDisplayMode DisplayMode,
    string TaskbarDeviceId,
    LyricsSourceSettings LyricsSources,
    bool LyricsMode,
    bool ShowTranslation,
    bool StartWithWindows)
{
    public static WidgetSettings Default { get; } = new(
        WidgetDisplayMode.FullDetails, "", LyricsSourceSettings.Default,
        LyricsMode: false, ShowTranslation: true, StartWithWindows: false);
}

/// <summary>
/// One <c>settings.json</c> for all of it - the only place settings are read
/// from and written to.
/// </summary>
internal static class WidgetSettingsStore
{
    private const int CurrentVersion = 1;

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskbarMusicControls", "settings.json");

    private static readonly string SettingsDirectory =
        Path.GetDirectoryName(SettingsPath)!;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Reads the settings. <paramref name="needsWrite"/> is true when the file
    /// was missing or did not carry every key, so the caller can write the
    /// defaults back once instead of on every start.
    /// </summary>
    public static WidgetSettings Load(out bool needsWrite)
    {
        needsWrite = true;
        try
        {
            if (!File.Exists(SettingsPath))
                return WidgetSettings.Default;

            using var stream = File.OpenRead(SettingsPath);
            var entry = JsonSerializer.Deserialize<Entry>(stream, Options);
            if (entry is not { Version: >= CurrentVersion })
                return WidgetSettings.Default;

            needsWrite =
                entry.DisplayMode is null ||
                entry.TaskbarDeviceId is null ||
                entry.LyricsPreferLocal is null ||
                entry.LyricsOrder is null ||
                entry.LyricsMode is null ||
                entry.ShowTranslation is null ||
                entry.StartWithWindows is null;

            return new WidgetSettings(
                entry.DisplayMode is { } mode && Enum.IsDefined(mode)
                    ? mode
                    : WidgetDisplayMode.FullDetails,
                entry.TaskbarDeviceId ?? "",
                new LyricsSourceSettings(
                    entry.LyricsPreferLocal ?? true,
                    entry.LyricsOrder ?? LyricsOnlineOrder.NeteaseFirst),
                entry.LyricsMode ?? false,
                entry.ShowTranslation ?? true,
                entry.StartWithWindows ?? false);
        }
        catch (Exception)
        {
            return WidgetSettings.Default;
        }
    }

    public static void Save(WidgetSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var tempPath = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = File.Create(tempPath))
                {
                    JsonSerializer.Serialize(stream, new Entry
                    {
                        Version = CurrentVersion,
                        DisplayMode = settings.DisplayMode,
                        TaskbarDeviceId = settings.TaskbarDeviceId,
                        LyricsPreferLocal = settings.LyricsSources.PreferLocal,
                        LyricsOrder = settings.LyricsSources.Order,
                        LyricsMode = settings.LyricsMode,
                        ShowTranslation = settings.ShowTranslation,
                        StartWithWindows = settings.StartWithWindows
                    }, Options);
                }

                File.Move(tempPath, SettingsPath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                catch (Exception) { }
            }
        }
        catch (Exception)
        {
            // Settings are optional: a failed write must not break the widget.
        }
    }

    /// <summary>
    /// Every value is nullable: a key that is absent is what tells the caller the
    /// file predates that setting and has to be rewritten.
    /// </summary>
    private sealed class Entry
    {
        public int Version { get; set; }
        public WidgetDisplayMode? DisplayMode { get; set; }
        public string? TaskbarDeviceId { get; set; }
        public bool? LyricsPreferLocal { get; set; }
        public LyricsOnlineOrder? LyricsOrder { get; set; }
        public bool? LyricsMode { get; set; }
        public bool? ShowTranslation { get; set; }
        public bool? StartWithWindows { get; set; }
    }
}
