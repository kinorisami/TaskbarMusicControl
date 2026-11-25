using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TaskbarMusicControls.Services;

internal static class LyricsOffsetStore
{
    private static readonly string OffsetDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskbarMusicControls", "Lyrics", "Offsets");

    public static TimeSpan Load(string trackIdentity)
    {
        try
        {
            var path = GetPath(trackIdentity);
            return File.Exists(path) && int.TryParse(File.ReadAllText(path), out var milliseconds)
                ? TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, -6000, 6000))
                : TimeSpan.Zero;
        }
        catch (Exception)
        {
            return TimeSpan.Zero;
        }
    }

    public static void Save(string trackIdentity, TimeSpan offset)
    {
        try
        {
            Directory.CreateDirectory(OffsetDirectory);
            File.WriteAllText(GetPath(trackIdentity),
                Math.Clamp((int)offset.TotalMilliseconds, -6000, 6000).ToString());
        }
        catch (Exception)
        {
            // Per-track timing adjustment is optional.
        }
    }

    private static string GetPath(string trackIdentity)
    {
        var normalized = trackIdentity.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(OffsetDirectory, hash + ".txt");
    }
}
