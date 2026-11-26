using System.Diagnostics;
using System.IO;

namespace TaskbarMusicControls.Services;

internal static class ArtworkDiagnostics
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskbarMusicControls", "Logs", "artwork.log");
    private const long MaxLogBytes = 512 * 1024;
    private static readonly LogSink Sink = new(LogPath, MaxLogBytes);

    [Conditional("DEBUG")]
    public static void Write(string message) =>
        Sink.Write($"{DateTimeOffset.Now:O} {message}");

    /// <summary>
    /// Errors are recorded in every build. A thumbnail that cannot be read
    /// leaves the widget without a cover and the user has no way to tell why,
    /// so those lines must survive a release build.
    /// </summary>
    public static void WriteError(string message) =>
        Sink.Write($"{DateTimeOffset.Now:O} ERROR {message}");
}
