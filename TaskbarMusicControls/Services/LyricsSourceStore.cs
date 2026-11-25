using System.IO;

namespace TaskbarMusicControls.Services;

internal static class LyricsSourceStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskbarMusicControls", "lyrics-source.txt");

    public static LyricsSourcePreference Load()
    {
        try
        {
            return File.Exists(SettingsPath) &&
                   Enum.TryParse<LyricsSourcePreference>(File.ReadAllText(SettingsPath), out var preference)
                ? preference
                : LyricsSourcePreference.Automatic;
        }
        catch (Exception)
        {
            return LyricsSourcePreference.Automatic;
        }
    }

    public static void Save(LyricsSourcePreference preference)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, preference.ToString());
        }
        catch (Exception)
        {
            // Source preference is optional.
        }
    }
}
