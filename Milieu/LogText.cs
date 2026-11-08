using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Milieu;

/// <summary>
/// Milieu's only logging surface: strings, never exception objects (an exception's message and stack can carry full
/// paths with the Windows user name, and users post dalamud.log publicly).
/// </summary>
public interface ILogSink
{
    void Debug(string message);
    void Information(string message);
    void Warning(string message);
    void Error(string message);
}

public static class LogText
{
    /// <summary>
    /// Replaces each sensitive path (an image path, the config directory) and its directories with just the final
    /// name, so log lines and messages never contain a full local path.
    /// </summary>
    public static string Sanitize(string text, params string?[] sensitivePaths)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var replacements = new List<(string From, string To)>();
        foreach (var p in sensitivePaths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            foreach (var variant in Variants(p))
            {
                var trimmed = variant.TrimEnd('\\', '/');
                if (trimmed.Length < 3) continue;
                replacements.Add((trimmed, Path.GetFileName(trimmed)));
                var dir = Path.GetDirectoryName(trimmed);
                while (!string.IsNullOrEmpty(dir) && dir.Length > 3)
                {
                    replacements.Add((dir, "…"));
                    dir = Path.GetDirectoryName(dir);
                }
            }
        }
        foreach (var (from, to) in replacements.OrderByDescending(r => r.From.Length))
            text = text.Replace(from, to, StringComparison.OrdinalIgnoreCase);
        return text;
    }

    /// <summary>Describes an exception for a log line: its type and sanitised message, no stack trace.</summary>
    public static string Describe(Exception ex, params string?[] sensitivePaths) =>
        Sanitize(ex.GetType().Name + ": " + ex.Message, sensitivePaths);

    private static IEnumerable<string> Variants(string p)
    {
        yield return p;
        yield return p.Replace('\\', '/');
        yield return p.Replace('/', '\\');
        string? full = null;
        try { full = Path.GetFullPath(p); } catch (Exception) { /* not a path */ }
        if (full != null && full != p) yield return full;
    }
}
