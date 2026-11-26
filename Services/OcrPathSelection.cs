using System;
using System.IO;

namespace MediaUnseal.Services;

internal static class OcrPathSelection
{
    private static string FilePath => Path.Combine(SubtitleOcr.ManagedRoot, "executable-path.txt");
    internal static event Action? Changed;

    internal static string? Read()
    {
        try
        {
            var path = File.ReadAllText(FilePath).Trim();
            return Path.IsPathFullyQualified(path) ? path : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static void Save(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute OCR executable path is required.", nameof(path));
        Directory.CreateDirectory(SubtitleOcr.ManagedRoot);
        var temporary = FilePath + ".tmp";
        try
        {
            File.WriteAllText(temporary, path);
            File.Move(temporary, FilePath, true);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Changed?.Invoke();
    }

    internal static void Clear()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
        Changed?.Invoke();
    }
}
