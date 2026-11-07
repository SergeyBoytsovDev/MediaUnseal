using System;
using System.Collections.Generic;
using System.IO;

namespace MediaUnseal.Services;

// A selected folder may itself be a user-chosen link. Its existing link chain is
// allowed, but a change to that chain or a generated child link stops the export.
internal sealed class OutputPathGuard
{
    private readonly string _root;
    private readonly (string Path, bool Reparse, string? Target, string? Resolved)[] _ancestors;
    private readonly StringComparison _comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal OutputPathGuard(string root)
    {
        _root = Path.GetFullPath(root);
        var ancestors = new List<(string, bool, string?, string?)>();
        for (var path = _root; path is not null; path = Path.GetDirectoryName(path))
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists) throw new DirectoryNotFoundException("The selected output directory is unavailable.");
            var reparse = (info.Attributes & FileAttributes.ReparsePoint) != 0;
            ancestors.Add((path, reparse, info.LinkTarget,
                reparse ? info.ResolveLinkTarget(true)?.FullName : null));
        }
        _ancestors = ancestors.ToArray();
    }

    internal void Validate(string directory)
    {
        foreach (var (path, reparse, target, resolved) in _ancestors)
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists || ((info.Attributes & FileAttributes.ReparsePoint) != 0) != reparse
                || !string.Equals(info.LinkTarget, target, _comparison)
                || reparse && !string.Equals(info.ResolveLinkTarget(true)?.FullName, resolved, _comparison))
                throw new IOException("The selected output directory changed during export.");
        }

        var relative = Path.GetRelativePath(_root, Path.GetFullPath(directory));
        if (Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("The output path escapes the selected directory.");
        if (relative == ".") return;
        var current = _root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            var info = new DirectoryInfo(current);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A generated output directory is unavailable or is a link.");
        }
    }
}
