using System.IO;
using System.Diagnostics;

namespace TaskbarMusicControls.Services;

internal static class TaskbarLayoutDiagnostics
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskbarMusicControls", "Logs", "taskbar-layout.log");
    private const long MaxLogBytes = 512 * 1024;
    private static readonly LogSink Sink = new(LogPath, MaxLogBytes);

    [Conditional("DEBUG")]
    public static void Write(string message) =>
        Sink.Write($"{DateTimeOffset.Now:O} {message}");
}
