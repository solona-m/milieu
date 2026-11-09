using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Milieu.Imaging;

namespace Milieu.Backdrop;

/// <summary>
/// Converted textures and generated VFX in <c>&lt;config&gt;/cache</c>, named by content hash so the game (which caches
/// resources by path) always loads the current file. Writes are atomic (tmp + move). Cleanup touches only Milieu's own
/// file patterns at the top of the cache directory.
/// </summary>
public sealed class CacheStore(string root)
{
    /// <summary>Bump when the conversion changes, so old cached textures are not reused.</summary>
    public const int ConverterVersion = 1;

    public string Root { get; } = root;

    public static string Hash16(byte[] data) => Convert.ToHexString(SHA256.HashData(data))[..16].ToLowerInvariant();

    public static string TextureKey(BackdropRequest r, ImageKind kind, long length, long lastWriteUtcTicks)
    {
        var sb = new StringBuilder();
        sb.Append(ConverterVersion).Append('|')
          .Append(Path.GetFullPath(r.ImagePath).ToLowerInvariant().Replace('\\', '/')).Append('|')
          .Append(length).Append('|').Append(lastWriteUtcTicks).Append('|').Append(r.MaxWidth);
        if (kind.IsHdr())
            sb.Append('|').Append(r.ExposureEv.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append((int)r.ToneMap);
        return Hash16(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    public static string AvfxKey(byte[] avfx) => Hash16(avfx);

    /// <summary>
    /// The light rig's cache key: the file only (path, length, write time) and the extractor version. Exposure, tone map
    /// and texture size don't change the image's radiance, so they are not part of it.
    /// </summary>
    public static string LightRigKey(string path, long length, long lastWriteUtcTicks) =>
        Hash16(Encoding.UTF8.GetBytes("L" + Lighting.LightExtractor.Version + "|" + Path.GetFullPath(path).ToLowerInvariant().Replace('\\', '/')
                                      + "|" + length + "|" + lastWriteUtcTicks));

    /// <summary>A subfolder, so <see cref="Cleanup"/> (top level only) never touches the rigs.</summary>
    public string LightRigDir => Path.Combine(Root, "lights");

    public string LightRigPath(string key) => Path.Combine(LightRigDir, $"lights_{key}.json");

    public static string TextureGamePath(string key) => $"vfx/milieu/{key}/sky.atex";
    public static string AvfxGamePath(string key) => $"vfx/milieu/{key}/dome.avfx";

    public string TextureDiskPath(string key) => Path.Combine(Root, $"sky_{key}.atex");
    public string AvfxDiskPath(string key) => Path.Combine(Root, $"dome_{key}.avfx");

    public bool TryGetTexture(string key, long expectedLength)
    {
        try
        {
            var f = new FileInfo(TextureDiskPath(key));
            return f.Exists && f.Length == expectedLength;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Writes through a tmp file and moves it into place; on any failure the tmp is deleted and nothing remains.</summary>
    public void WriteAtomic(string path, Action<Stream> write) => WriteAtomic(path, write, CancellationToken.None);

    /// <summary>
    /// As <see cref="WriteAtomic(string, Action{Stream})"/>, and the token is checked after the write and before the move,
    /// so a cancelled write never leaves a final file (synced staging: a superseded job must not publish its file).
    /// </summary>
    public void WriteAtomic(string path, Action<Stream> write, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var s = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) write(s);
            ct.ThrowIfCancellationRequested();
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    public void WriteAtomic(string path, byte[] bytes) => WriteAtomic(path, s => s.Write(bytes));

    /// <summary>Deletes Milieu's cache files except those named in <paramref name="keep"/>. Locked files are skipped.</summary>
    public int Cleanup(IReadOnlySet<string> keep)
    {
        if (!Directory.Exists(Root)) return 0;
        var deleted = 0;
        foreach (var pattern in new[] { "sky_*.atex", "dome_*.avfx", "*.tmp" })
            foreach (var f in Directory.EnumerateFiles(Root, pattern, SearchOption.TopDirectoryOnly))
            {
                if (keep.Contains(Path.GetFileName(f))) continue;
                if (TryDelete(f)) deleted++;
            }
        return deleted;
    }

    internal static bool TryDelete(string path)
    {
        try { File.Delete(path); return !File.Exists(path); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
