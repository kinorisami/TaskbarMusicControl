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
    bool StartWithWindows,
    bool HideWhenIdle,
    string PlayerId,
    WidgetBlock Block,
    WidgetAlignment Alignment,
    bool TaskbarCentered)
{
    public static WidgetSettings Default { get; } = new(
        WidgetDisplayMode.Full, "", LyricsSourceSettings.Default,
        LyricsMode: false, ShowTranslation: true, StartWithWindows: false,
        HideWhenIdle: false, PlayerId: "", Block: WidgetBlock.Right, Alignment: WidgetAlignment.Right,
        TaskbarCentered: false);
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

            // A display mode this build does not know is replaced by the default
            // and the file is rewritten once, so it stops carrying the old name.
            var displayMode = ParseDisplayMode(entry.DisplayMode);

            needsWrite =
                entry.DisplayMode != displayMode.ToString() ||
                entry.TaskbarDeviceId is null ||
                entry.LyricsPreferLocal is null ||
                entry.LyricsOrder is null ||
                entry.LyricsMode is null ||
                entry.ShowTranslation is null ||
                entry.StartWithWindows is null ||
                entry.HideWhenIdle is null ||
                entry.PlayerId is null ||
                entry.Block is null ||
                entry.Alignment is null ||
                entry.TaskbarCentered is null;

            return new WidgetSettings(
                displayMode,
                entry.TaskbarDeviceId ?? "",
                new LyricsSourceSettings(
                    entry.LyricsPreferLocal ?? true,
                    entry.LyricsOrder ?? LyricsOnlineOrder.NeteaseFirst),
                entry.LyricsMode ?? false,
                entry.ShowTranslation ?? true,
                entry.StartWithWindows ?? false,
                entry.HideWhenIdle ?? false,
                entry.PlayerId ?? "",
                entry.Block is { } block && Enum.IsDefined(block)
                    ? block
                    : WidgetBlock.Right,
                entry.Alignment is { } alignment && Enum.IsDefined(alignment)
                    ? alignment
                    : WidgetAlignment.Right,
                entry.TaskbarCentered ?? false);
        }
        catch (Exception)
        {
            return WidgetSettings.Default;
        }
    }

    /// <summary>
    /// The display mode is read as a name so a value this build does not know -
    /// a mode that has been dropped since the file was written - falls back to
    /// the default instead of failing the whole file.
    /// </summary>
    private static WidgetDisplayMode ParseDisplayMode(string? value) =>
        Enum.TryParse<WidgetDisplayMode>(value, out var mode) && Enum.IsDefined(mode)
            ? mode
            : WidgetDisplayMode.Full;

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
                        DisplayMode = settings.DisplayMode.ToString(),
                        TaskbarDeviceId = settings.TaskbarDeviceId,
                        LyricsPreferLocal = settings.LyricsSources.PreferLocal,
                        LyricsOrder = settings.LyricsSources.Order,
                        LyricsMode = settings.LyricsMode,
                        ShowTranslation = settings.ShowTranslation,
                        StartWithWindows = settings.StartWithWindows,
                        HideWhenIdle = settings.HideWhenIdle,
                        PlayerId = settings.PlayerId,
                        Block = settings.Block,
                        Alignment = settings.Alignment,
                        TaskbarCentered = settings.TaskbarCentered
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
        public string? DisplayMode { get; set; }
        public string? TaskbarDeviceId { get; set; }
        public bool? LyricsPreferLocal { get; set; }
        public LyricsOnlineOrder? LyricsOrder { get; set; }
        public bool? LyricsMode { get; set; }
        public bool? ShowTranslation { get; set; }
        public bool? StartWithWindows { get; set; }
        public bool? HideWhenIdle { get; set; }
        public string? PlayerId { get; set; }
        public WidgetBlock? Block { get; set; }
        public WidgetAlignment? Alignment { get; set; }
        public bool? TaskbarCentered { get; set; }
    }
}
