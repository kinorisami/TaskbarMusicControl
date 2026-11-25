using System.IO;

namespace TaskbarMusicControls.Services;

internal static class TaskbarSelectionStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskbarMusicControls", "taskbar.txt");

    public static string? Load()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? File.ReadAllText(SettingsPath).Trim()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Save(string deviceId)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, deviceId);
        }
        catch (Exception)
        {
            // Taskbar selection is optional.
        }
    }
}
