using System.IO;

namespace TaskbarMusicControls.Services;

internal static class TaskbarLayoutDiagnostics
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskbarMusicControls", "Logs", "taskbar-layout.log");
    private const long MaxLogBytes = 512 * 1024;
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length >= MaxLogBytes)
                {
                    var previous = LogPath + ".1";
                    if (File.Exists(previous))
                        File.Delete(previous);
                    File.Move(LogPath, previous);
                }

                File.AppendAllText(LogPath,
                    $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Layout diagnostics must never break the widget.
        }
    }
}
