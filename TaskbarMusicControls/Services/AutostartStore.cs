using System.IO;
using Microsoft.Win32;

namespace TaskbarMusicControls.Services;

/// <summary>
/// Registers the widget in the per-user Run key, which needs no elevation and
/// covers every account the widget is used from.
/// </summary>
internal static class AutostartStore
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TaskbarMusicControls";

    /// <summary>The command line currently registered, or null when there is none.</summary>
    public static string? RegisteredCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Points the Run entry at this executable, or removes it.</summary>
    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
                return;

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
                return;

            key.SetValue(ValueName, $"\"{executable}\"");
        }
        catch (Exception)
        {
            // The setting stays in settings.json; the registry is best effort.
        }
    }

    /// <summary>
    /// Whether the Run entry already points at this executable. Used to avoid
    /// rewriting the registry on every start.
    /// </summary>
    public static bool IsRegistered()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
            return false;

        var registered = RegisteredCommand();
        return registered is not null &&
            string.Equals(registered.Trim('"'), executable, StringComparison.OrdinalIgnoreCase);
    }
}
