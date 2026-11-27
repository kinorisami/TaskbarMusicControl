using System.IO;

namespace TaskbarMusicControls.Services;

/// <summary>
/// The global source setting: whether the local lyric files win over the online
/// providers, and which online provider is asked first.
/// </summary>
internal static class LyricsSourceStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskbarMusicControls", "lyrics-source.txt");

    public static LyricsSourceSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return LyricsSourceSettings.Default;

            var text = File.ReadAllText(SettingsPath).Trim();

            // Versions before the ordering setting stored one of the old
            // preference names; map them onto the closest new setting.
            if (!text.Contains('='))
            {
                return text switch
                {
                    "Netease" => new LyricsSourceSettings(false, LyricsOnlineOrder.NeteaseFirst),
                    "Lrclib" => new LyricsSourceSettings(false, LyricsOnlineOrder.LrclibFirst),
                    _ => LyricsSourceSettings.Default
                };
            }

            var preferLocal = true;
            var order = LyricsOnlineOrder.NeteaseFirst;
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();

                if (key.Equals("PreferLocal", StringComparison.OrdinalIgnoreCase) &&
                    bool.TryParse(value, out var local))
                    preferLocal = local;
                else if (key.Equals("Order", StringComparison.OrdinalIgnoreCase) &&
                    Enum.TryParse<LyricsOnlineOrder>(value, out var parsed))
                    order = parsed;
            }

            return new LyricsSourceSettings(preferLocal, order);
        }
        catch (Exception)
        {
            return LyricsSourceSettings.Default;
        }
    }

    public static void Save(LyricsSourceSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath,
                $"PreferLocal={settings.PreferLocal}{Environment.NewLine}Order={settings.Order}");
        }
        catch (Exception)
        {
            // The source setting is optional.
        }
    }
}
