using System.IO;

namespace MediaUnseal.Services;

internal static class AppDirectories
{
    internal static string Resolve(string root, string? settingsFile = null)
    {
        var current = Path.Combine(root, "MediaUnseal");
        var previous = Path.Combine(root, "TrackExtractor");
        if (settingsFile is not null)
        {
            if (File.Exists(Path.Combine(current, settingsFile))) return current;
            if (File.Exists(Path.Combine(previous, settingsFile))) return previous;
        }
        if (Directory.Exists(current)) return current;
        return Directory.Exists(previous) ? previous : current;
    }
}
