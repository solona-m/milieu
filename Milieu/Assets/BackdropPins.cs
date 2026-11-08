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
        new("forest", "Forest", 44739312L, "80b81b010da5c49db392db65b0837d94d952c69419d54f794424a8f33e0eaa29"),
        new("galaxy", "Galaxy", 44739312L, "5212832130fcb3916f4a05420b9a5f4c002896b52c9ecfc300759976ab50ec4b"),
        new("livingroom", "Living Room", 44739312L, "708926c13a82ecc5c5ebd8a4c8c89a753d760798ff2f6f17e15ae55037226b31"),
        new("livingroom2", "Living Room 2", 44739312L, "8727593be0652bf33c7b0827b79374613cac7322b9e81c26448e7a4fde90708d"),
        new("lodge", "Lodge", 44739312L, "ec19e94f6a4964e9d752678d5f995988b9d175571e3c060c50b16fe13ff59572"),
        new("moon", "Moon", 44739312L, "74d3deb3519573150760287fd876eed2fa88f8735778e11456515a353bd64f5f"),
        new("sunset", "Sunset", 44739312L, "f646b04d1a92e8436a3857991b60f3ff865acd5883832e61ba6f6670028372bb"),
        new("warehouse", "Warehouse", 44739312L, "6644700b04f9237f0c1ca07b21e66388075c26a9b827d75092e6c72fac7f2c92"),
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
