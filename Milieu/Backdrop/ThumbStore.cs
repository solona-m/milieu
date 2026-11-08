using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Milieu.Backdrop;

/// <summary>
/// Thumbnails of the user's own images: the 256x128 level of their conversion's mip chain, saved as raw RGBA
/// (131,072 bytes) in <c>&lt;config&gt;/cache/thumbs/thumb_&lt;key&gt;.rgba</c>, keyed by path, length and modification time
/// so an edited image gets a fresh one. Pure file logic; the window turns the bytes into a texture.
/// </summary>
public sealed class ThumbStore(string root)
{
    public const int Width = TextureConversion.ThumbWidth;
    public const int Height = TextureConversion.ThumbHeight;
    public const int Bytes = Width * Height * 4;

    public string Root { get; } = root;

    /// <summary>The key for an image's current version, or null when the file is missing.</summary>
    public static string? Key(string imagePath)
    {
        try
        {
            var f = new FileInfo(imagePath);
            if (!f.Exists) return null;
            return CacheStore.Hash16(Encoding.UTF8.GetBytes($"{Path.GetFullPath(imagePath).ToLowerInvariant()}|{f.Length}|{f.LastWriteTimeUtc.Ticks}"));
        }
        catch (Exception) { return null; }
    }

    public string PathFor(string key) => Path.Combine(Root, $"thumb_{key}.rgba");

    public void Save(string imagePath, byte[] rgba)
    {
        if (rgba.Length != Bytes) throw new ArgumentException($"A thumbnail is {Bytes} bytes of RGBA.", nameof(rgba));
        if (Key(imagePath) is not { } key) return;
        new CacheStore(Root).WriteAtomic(PathFor(key), rgba);
    }

    /// <summary>The current thumbnail of an image, or null (not converted yet, edited since, or unreadable).</summary>
    public byte[]? TryLoad(string imagePath)
    {
        if (Key(imagePath) is not { } key) return null;
        try
        {
            var p = PathFor(key);
            return File.Exists(p) && new FileInfo(p).Length == Bytes ? File.ReadAllBytes(p) : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Deletes thumbnails (own pattern only) that no current user image refers to.</summary>
    public int Cleanup(IEnumerable<string> userImages)
    {
        if (!Directory.Exists(Root)) return 0;
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var img in userImages) if (Key(img) is { } k) keep.Add(Path.GetFileName(PathFor(k)));
        var deleted = 0;
        foreach (var f in Directory.EnumerateFiles(Root, "thumb_*.rgba", SearchOption.TopDirectoryOnly))
        {
            if (keep.Contains(Path.GetFileName(f))) continue;
            if (CacheStore.TryDelete(f)) deleted++;
        }
        return deleted;
    }
}
