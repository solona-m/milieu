using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Milieu.Backdrop;
using Milieu.Imaging;
using Milieu.Sync;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public sealed class SyncStagingTests : IDisposable
{
    private readonly TempDir dir = new();

    public void Dispose()
    {
        foreach (var d in Directory.EnumerateDirectories(dir.Path, "*", SearchOption.AllDirectories))
            try { new DirectoryInfo(d).Attributes = FileAttributes.Directory; } catch (IOException) { }
        dir.Dispose();
    }

    private PreparedTexture Source(string key = "00112233445566ff", int length = 3000)
    {
        var src = dir.File($"src_{key}.atex");
        File.WriteAllBytes(src, Enumerable.Range(0, length).Select(i => (byte)i).ToArray());
        return new PreparedTexture(key, src, CacheStore.TextureGamePath(key), 1024, 512, ImageKind.Jpeg, []);
    }

    [Fact] // [EDGE-synced-delivery-18]
    public void Root_IsDirectChildWithoutExcludedSegments()
    {
        var mods = dir.File("mods");
        var s = new SyncStaging(mods);
        Assert.Equal(Path.Combine(mods, "_milieu_sync"), s.Root);
        Assert.Equal(mods, Path.GetDirectoryName(s.Root));
        var name = Path.GetFileName(s.Root);
        Assert.All(name, c => Assert.True(c < 128));
        foreach (var seg in new[] { "bg", "bgcommon", "ui" })
            Assert.DoesNotContain(seg, s.Root[mods.Length..].Split('\\', '/'), StringComparer.OrdinalIgnoreCase);
    }

    [Fact] // [EDGE-synced-delivery-17]
    public void EnsureFolder_SetsHidden_AlsoWhenExistingVisible()
    {
        var s = new SyncStaging(dir.File("mods"));
        s.EnsureFolder();
        Assert.True(new DirectoryInfo(s.Root).Attributes.HasFlag(FileAttributes.Hidden));
        new DirectoryInfo(s.Root).Attributes = FileAttributes.Directory;
        s.EnsureFolder();
        Assert.True(new DirectoryInfo(s.Root).Attributes.HasFlag(FileAttributes.Hidden));
    }

    [Fact] // [EDGE-synced-delivery-21]
    public void Stage_SkipsSameLength_ReplacesDifferent()
    {
        var s = new SyncStaging(dir.File("mods"));
        var tex = Source();
        var staged = s.StageAtex(tex, default);
        Assert.Equal(s.TexturePath(tex.Key), staged.DiskPath);
        Assert.Equal(File.ReadAllBytes(tex.DiskPath), File.ReadAllBytes(staged.DiskPath));

        var opened = 0;
        s.OpenSource = p => { opened++; return File.OpenRead(p); };
        File.WriteAllBytes(staged.DiskPath, new byte[3000]);          // same length → no copy
        s.StageAtex(tex, default);
        Assert.Equal(0, opened);
        Assert.Equal(new byte[3000], File.ReadAllBytes(staged.DiskPath));

        File.WriteAllBytes(staged.DiskPath, new byte[10]);            // different length → atomic replace
        s.StageAtex(tex, default);
        Assert.Equal(1, opened);
        Assert.Equal(File.ReadAllBytes(tex.DiskPath), File.ReadAllBytes(staged.DiskPath));
        Assert.Empty(Directory.EnumerateFiles(s.Root, "*.tmp"));
    }

    /// <summary>A stream that cancels the token after its first read (a slow copy cancelled mid-way).</summary>
    private sealed class CancellingStream(Stream inner, CancellationTokenSource cts) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, Math.Min(count, 100));
            cts.Cancel();
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    [Fact] // [EDGE-synced-delivery-22]
    public void Stage_Cancelled_DeletesTmp()
    {
        var s = new SyncStaging(dir.File("mods"));
        var tex = Source();
        using var cts = new CancellationTokenSource();
        s.OpenSource = p => new CancellingStream(File.OpenRead(p), cts);
        Assert.ThrowsAny<OperationCanceledException>(() => s.StageAtex(tex, cts.Token));
        Assert.False(File.Exists(s.TexturePath(tex.Key)));
        Assert.Empty(Directory.EnumerateFiles(s.Root, "*.tmp"));
    }

    [Fact] // [EDGE-synced-delivery-2]
    public void CopyFailure_LeavesNoTmp()
    {
        var s = new SyncStaging(dir.File("mods"));
        var tex = Source();
        s.OpenSource = _ => throw new IOException("disk full");
        Assert.Throws<IOException>(() => s.StageAtex(tex, default));
        Assert.False(File.Exists(s.TexturePath(tex.Key)));
        Assert.Empty(Directory.EnumerateFiles(s.Root, "*.tmp"));
    }

    [Fact] // [EDGE-synced-delivery-16]
    public void Cleanup_OnlyMilieuPatterns()
    {
        var s = new SyncStaging(dir.File("mods"));
        s.EnsureFolder();
        foreach (var f in new[] { "sky_a.atex", "dome_b.avfx", "x.tmp", "notes.txt", "sky_keep.atex" })
            File.WriteAllBytes(Path.Combine(s.Root, f), [1]);
        Directory.CreateDirectory(Path.Combine(s.Root, "sub"));
        File.WriteAllBytes(Path.Combine(s.Root, "sub", "sky_c.atex"), [1]);
        File.WriteAllBytes(Path.Combine(dir.File("mods"), "sky_outside.atex"), [1]);
        Assert.Equal(3, s.Store.Cleanup(new HashSet<string> { "sky_keep.atex" }));
        Assert.Equal(["notes.txt", "sky_keep.atex"], Directory.EnumerateFiles(s.Root).Select(f => Path.GetFileName(f)).Order().ToArray());
        Assert.True(File.Exists(Path.Combine(s.Root, "sub", "sky_c.atex")));
        Assert.True(File.Exists(Path.Combine(dir.File("mods"), "sky_outside.atex")));
    }

    [Fact] // [EDGE-synced-delivery-15]
    public void Cleanup_SkipsLockedFile()
    {
        var s = new SyncStaging(dir.File("mods"));
        s.EnsureFolder();
        var locked = Path.Combine(s.Root, "sky_locked.atex");
        File.WriteAllBytes(locked, [1]);
        File.WriteAllBytes(Path.Combine(s.Root, "dome_x.avfx"), [1]);
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(1, s.Store.Cleanup(new HashSet<string>()));
        Assert.True(File.Exists(locked));
    }

    [Fact] // [EDGE-synced-delivery-16] [EDGE-synced-delivery-35]
    public void Cleanup_MissingFolder_NotCreated()
    {
        var s = new SyncStaging(dir.File("mods"));
        Assert.Equal(0, s.Store.Cleanup(new HashSet<string>()));
        Assert.False(s.Exists);
    }
}
