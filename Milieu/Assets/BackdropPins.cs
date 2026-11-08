using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Milieu.GameFiles;

namespace Milieu.Assets;

/// <summary>One bundled scene as published: a pre-converted 8192x4096 BC7 .atex.</summary>
public sealed record BackdropPin(string Stem, string DisplayName, long Bytes, string Sha256)
{
    public string FileName => Stem + ".atex";
}

/// <summary>
/// The pinned bundled scenes (pure; linked into tools/PrepareBackdrops). Assets are immutable per tag: changing an
/// image means new pins and a new <see cref="Tag"/>. The pins come from `PrepareBackdrops atex` (docs/adr/0005).
/// </summary>
public static class BackdropPins
{
    public const string Tag = "backdrops-v2";
    public const string IdPrefix = "bundled:";
    public const int Width = 8192;
    public const int Height = 4096;

    public static readonly IReadOnlyList<BackdropPin> All =
    [
        new("forest", "Forest", 44739312L, "PENDING_FOREST"),
        new("galaxy", "Galaxy", 44739312L, "PENDING_GALAXY"),
        new("livingroom", "Living Room", 44739312L, "PENDING_LIVINGROOM"),
        new("livingroom2", "Living Room 2", 44739312L, "PENDING_LIVINGROOM2"),
        new("lodge", "Lodge", 44739312L, "PENDING_LODGE"),
        new("moon", "Moon", 44739312L, "PENDING_MOON"),
        new("sunset", "Sunset", 44739312L, "PENDING_SUNSET"),
        new("warehouse", "Warehouse", 44739312L, "PENDING_WAREHOUSE"),
    ];

    public static int Count => All.Count;

    public static long TotalBytes => All.Sum(p => p.Bytes);

    public static string CreditsUrl => "https://github.com/" + MilieuAssets.Repo + "/blob/" + Tag + "/Milieu/DefaultBackdrops/CREDITS.md";

    public static BackdropPin? Find(string stem) => All.FirstOrDefault(p => string.Equals(p.Stem, stem, StringComparison.OrdinalIgnoreCase));

    /// <summary>The asset name to ask for; GitHub rewrites spaces (none of ours have any).</summary>
    public static string RemoteName(BackdropPin pin) => pin.FileName.Replace(' ', '.');

    public static string IdOf(string stem) => IdPrefix + stem;

    /// <summary>True for "bundled:&lt;stem&gt;" naming a pinned scene.</summary>
    public static bool TryParseId(string? imagePath, out BackdropPin pin)
    {
        pin = null!;
        if (imagePath == null || !imagePath.StartsWith(IdPrefix, StringComparison.Ordinal)) return false;
        var found = Find(imagePath[IdPrefix.Length..]);
        if (found == null) return false;
        pin = found;
        return true;
    }

    public static bool IsBundledId(string? imagePath) => imagePath != null && imagePath.StartsWith(IdPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Checks a directory of .atex files against the pins: exactly the pinned stems, each at its size, header and
    /// SHA-256, and the expected tag. Returns the problems (empty = good).
    /// </summary>
    public static List<string> Verify(string dir, string tag)
    {
        var problems = new List<string>();
        if (tag != Tag) problems.Add($"tag '{tag}' does not match the pinned tag '{Tag}'");
        if (!Directory.Exists(dir)) { problems.Add($"directory not found: {Path.GetFileName(dir)}"); return problems; }
        var present = Directory.GetFiles(dir, "*.atex").Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var extra in present.Where(s => Find(s!) == null)) problems.Add($"{extra}.atex is not pinned");
        var header = AtexWriter.Header(AtexWriter.Layout(Width, Height));
        foreach (var pin in All)
        {
            var path = Path.Combine(dir, pin.FileName);
            if (!File.Exists(path)) { problems.Add($"{pin.FileName} is missing"); continue; }
            var len = new FileInfo(path).Length;
            if (len != pin.Bytes) { problems.Add($"{pin.FileName} is {len} bytes, pinned {pin.Bytes}"); continue; }
            using var fs = File.OpenRead(path);
            var head = new byte[header.Length];
            fs.ReadExactly(head);
            if (!head.AsSpan().SequenceEqual(header)) { problems.Add($"{pin.FileName} has an unexpected header"); continue; }
            fs.Position = 0;
            var sha = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            if (sha != pin.Sha256) problems.Add($"{pin.FileName} sha256 {sha[..16]}… does not match the pin");
        }
        return problems;
    }
}
