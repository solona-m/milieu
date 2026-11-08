using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Milieu.Backdrop;

namespace Milieu.Sync;

/// <summary>
/// Milieu's hidden folder inside Penumbra's mod directory, <c>&lt;mod dir&gt;\_milieu_sync</c>. PSync uploads only files
/// under the mod directory (or its own cache), so synced dome files must live here as real files. The folder is marked
/// Hidden, which Penumbra's mod scan skips; its name is ASCII and has no bg/bgcommon/ui segment (PSync's scan skips those).
/// Writes and cleanup go through a <see cref="CacheStore"/> rooted here (same names, same atomic writes, same patterns).
/// </summary>
public sealed class SyncStaging
{
    public const string FolderName = "_milieu_sync";

    public SyncStaging(string modDirectory)
    {
        Root = Path.Combine(modDirectory, FolderName);
        Store = new CacheStore(Root);
    }

    public string Root { get; }

    public CacheStore Store { get; }

    /// <summary>Test seam: opens the source texture for the staging copy.</summary>
    internal Func<string, Stream> OpenSource { get; set; } = File.OpenRead;

    public bool Exists => Directory.Exists(Root);

    /// <summary>Creates the folder (if needed) and marks it Hidden, also when it exists without the attribute.</summary>
    public void EnsureFolder()
    {
        var info = Directory.CreateDirectory(Root);
        if ((info.Attributes & FileAttributes.Hidden) == 0) info.Attributes |= FileAttributes.Hidden;
    }

    /// <summary>The staged texture path for a texture key.</summary>
    public string TexturePath(string key) => Store.TextureDiskPath(key);

    /// <summary>
    /// Copies the prepared texture to <c>sky_&lt;key&gt;.atex</c> (skipped when that file exists with the same length), on the
    /// thread pool. A token cancelled during or after the copy leaves no final file.
    /// </summary>
    public Task<PreparedTexture> StageAtexAsync(PreparedTexture tex, CancellationToken ct) => Task.Run(() => StageAtex(tex, ct), ct);

    /// <summary>The staging copy itself, on the calling thread (the controller runs it inside its background job).</summary>
    public PreparedTexture StageAtex(PreparedTexture tex, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnsureFolder();
        var target = TexturePath(tex.Key);
        var sourceLength = new FileInfo(tex.DiskPath).Length;
        var existing = new FileInfo(target);
        if (!(existing.Exists && existing.Length == sourceLength))
        {
            Store.WriteAtomic(target, dst =>
            {
                using var src = OpenSource(tex.DiskPath);
                var buf = new byte[1 << 20];
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    dst.Write(buf, 0, n);
                }
            }, ct);
        }
        return tex with { DiskPath = target };
    }
}
