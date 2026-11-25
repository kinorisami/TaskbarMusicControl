using System.IO;
using TaskbarMusicControls.Overlay;

namespace TaskbarMusicControls.Services;

internal static class WidgetDisplayModeStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskbarMusicControls", "display-mode.txt");

    public static WidgetDisplayMode Load()
    {
        try
        {
            return File.Exists(SettingsPath) &&
                   Enum.TryParse<WidgetDisplayMode>(File.ReadAllText(SettingsPath), out var mode)
                ? mode
                : WidgetDisplayMode.FullDetails;
        }
        catch (Exception)
        {
            return WidgetDisplayMode.FullDetails;
        }
    }

    public static void Save(WidgetDisplayMode mode)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, mode.ToString());
        }
        catch (Exception)
        {
            // Layout preference is optional.
        }
    }
}
