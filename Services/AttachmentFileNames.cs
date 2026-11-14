using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaUnseal.Services;

internal static class AttachmentFileNames
{
    internal static string Safe(string name)
    {
        var leaf = name.Replace('\\', '/').Split('/').LastOrDefault() ?? "";
        var text = new StringBuilder(leaf.Length);
        foreach (var c in leaf)
            text.Append(char.IsControl(c) || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'
                ? '_' : c);
        var safe = text.ToString().Trim(' ', '.');
        if (safe.Length > 120) safe = safe[..120].TrimEnd(' ', '.');
        if (Regex.IsMatch(safe.Split('.')[0],
                @"\A(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|(?:COM|LPT)[1-9¹²³])\z",
                RegexOptions.IgnoreCase)) safe = "_" + safe;
        return safe.Length == 0 ? "attachment" : safe;
    }
}
