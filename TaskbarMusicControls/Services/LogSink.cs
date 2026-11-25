using System.Collections.Concurrent;
using System.IO;

namespace TaskbarMusicControls.Services;

/// <summary>
/// Writes diagnostic lines to a rotating file on a background thread so logging
/// never blocks the media/timeline/UI path on disk I/O.
/// </summary>
internal sealed class LogSink
{
    private readonly BlockingCollection<string> _queue = new();
    private readonly string _path;
    private readonly long _maxBytes;

    public LogSink(string path, long maxBytes)
    {
        _path = path;
        _maxBytes = maxBytes;
        var thread = new Thread(Drain) { IsBackground = true };
        thread.Start();
    }

    public void Write(string message)
    {
        try
        {
            _queue.Add(message);
        }
        catch (InvalidOperationException)
        {
            // The queue has been marked as complete during process shutdown.
        }
    }

    private void Drain()
    {
        foreach (var message in _queue.GetConsumingEnumerable())
        {
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var info = new FileInfo(_path);
                if (info.Exists && info.Length >= _maxBytes)
                {
                    var previous = _path + ".1";
                    if (File.Exists(previous))
                        File.Delete(previous);
                    File.Move(_path, previous);
                }

                File.AppendAllText(_path, message + Environment.NewLine);
            }
            catch (Exception)
            {
                // Diagnostics must never break the widget.
            }
        }
    }
}
