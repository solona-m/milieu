using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Milieu.Backdrop;

namespace Milieu.Sync;

/// <summary>
/// Milieu's folder inside Penumbra's mod directory, <c>&lt;mod dir&gt;\_milieu_sync</c>: also the real "Milieu Sync" mod
/// (<see cref="SyncRealMod"/>), so it is visible (an older build's Hidden attribute is cleared). PSync uploads only files
/// under the mod directory (or its own cache), so synced dome files must live here as real files; the name is ASCII and has
/// no bg/bgcommon/ui segment (PSync's scan skips those). Staged files go through a <see cref="CacheStore"/> rooted here
/// (same names, same atomic writes, same cleanup patterns); cleanup never touches meta.json or default_mod.json.
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

    /// <summary>Test seam: runs after the "already staged" check, before the re-check (a concurrent cleanup).</summary>
    internal Action<string>? AfterSkipCheck { get; set; }

    public bool Exists => Directory.Exists(Root);

    /// <summary>Creates the folder if needed. It is a real Penumbra mod ("Milieu Sync"), so it is not hidden.</summary>
    public void EnsureFolder()
    {
        var info = Directory.CreateDirectory(Root);
        if ((info.Attributes & FileAttributes.Hidden) != 0) info.Attributes &= ~FileAttributes.Hidden;
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
        var skip = existing.Exists && existing.Length == sourceLength;
        if (skip)
        {
            // Another job's cleanup may have removed the file since the check: re-check, and copy if it's gone.
            AfterSkipCheck?.Invoke(target);
            skip = File.Exists(target);
        }
        if (!skip)
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
