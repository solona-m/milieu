using System;
using System.Collections.Generic;
using System.IO;
using Milieu.Backdrop;
using Milieu.Imaging;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public class CacheStoreTests
{
    [Fact] // [EDGE-hdri-backdrop-17]
    public void WriteAtomic_FailureDeletesTmp()
    {
        using var t = new TempDir();
        var cache = new CacheStore(t.File("cache"));
        var target = cache.TextureDiskPath("abc");
        Assert.Throws<IOException>(() => cache.WriteAtomic(target, s => { s.Write([1, 2, 3]); throw new IOException("disk full"); }));
        Assert.Empty(Directory.GetFiles(cache.Root));

        cache.WriteAtomic(target, [9, 9]);
        Assert.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(target));
        Assert.Single(Directory.GetFiles(cache.Root));
    }

    [Fact] // [EDGE-hdri-backdrop-26]
    public void Cleanup_OnlyOwnPattern_KeepsCurrent_SkipsLocked()
    {
        using var t = new TempDir();
        var cache = new CacheStore(t.File("cache"));
        Directory.CreateDirectory(Path.Combine(cache.Root, "sub"));
        foreach (var f in new[] { "sky_a.atex", "sky_b.atex", "dome_c.avfx", "x.tmp", "notes.txt", "sub/sky_d.atex", "dome_locked.avfx" })
            File.WriteAllBytes(Path.Combine(cache.Root, f), [1]);
        using (File.Open(Path.Combine(cache.Root, "dome_locked.avfx"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var deleted = cache.Cleanup(new HashSet<string> { "sky_a.atex" });
            Assert.Equal(3, deleted);
        }
        Assert.True(File.Exists(Path.Combine(cache.Root, "sky_a.atex")));
        Assert.True(File.Exists(Path.Combine(cache.Root, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(cache.Root, "sub", "sky_d.atex")));
        Assert.True(File.Exists(Path.Combine(cache.Root, "dome_locked.avfx")));
        Assert.False(File.Exists(Path.Combine(cache.Root, "sky_b.atex")));
        Assert.False(File.Exists(Path.Combine(cache.Root, "x.tmp")));
        Assert.Equal(0, new CacheStore(t.File("missing")).Cleanup(new HashSet<string>()));
    }

    [Fact] // [EDGE-hdri-backdrop-26]
    public void Keys_ChangeWithInputs()
    {
        var r = new BackdropRequest(@"C:\p\pano.hdr", 8192, 0, ToneMapOperator.Aces);
        var k = CacheStore.TextureKey(r, ImageKind.Hdr, 100, 5);
        Assert.Matches("^[0-9a-f]{16}$", k);
        Assert.NotEqual(k, CacheStore.TextureKey(r, ImageKind.Hdr, 100, 6));
        Assert.NotEqual(k, CacheStore.TextureKey(r, ImageKind.Hdr, 101, 5));
        Assert.NotEqual(k, CacheStore.TextureKey(r with { ImagePath = @"C:\p\other.hdr" }, ImageKind.Hdr, 100, 5));
        Assert.NotEqual(k, CacheStore.TextureKey(r with { ExposureEv = 1 }, ImageKind.Hdr, 100, 5));
        Assert.NotEqual(k, CacheStore.TextureKey(r with { ToneMap = ToneMapOperator.None }, ImageKind.Hdr, 100, 5));
        Assert.NotEqual(k, CacheStore.TextureKey(r with { MaxWidth = 4096 }, ImageKind.Hdr, 100, 5));

        var j = new BackdropRequest(@"C:\p\pano.jpg", 8192, 0, ToneMapOperator.Aces);
        var kj = CacheStore.TextureKey(j, ImageKind.Jpeg, 100, 5);
        Assert.Equal(kj, CacheStore.TextureKey(j with { ExposureEv = 3, ToneMap = ToneMapOperator.None }, ImageKind.Jpeg, 100, 5));

        Assert.Matches("^vfx/milieu/[0-9a-f]{16}/sky\\.atex$", CacheStore.TextureGamePath(k));
        Assert.Matches("^vfx/milieu/[0-9a-f]{16}/dome\\.avfx$", CacheStore.AvfxGamePath(CacheStore.AvfxKey([1, 2])));
    }

    [Fact] // [AC-exr-support-3] [EDGE-exr-support-32]
    public void ExrKey_IncludesExposureAndToneMap()
    {
        var r = new BackdropRequest(@"C:\p\pano.exr", 8192, 0, ToneMapOperator.Aces);
        var k = CacheStore.TextureKey(r, ImageKind.Exr, 100, 5);
        Assert.Equal(k, CacheStore.TextureKey(r with { }, ImageKind.Exr, 100, 5));
        Assert.NotEqual(k, CacheStore.TextureKey(r with { ExposureEv = 1.5f }, ImageKind.Exr, 100, 5));
        Assert.NotEqual(k, CacheStore.TextureKey(r with { ToneMap = ToneMapOperator.Reinhard }, ImageKind.Exr, 100, 5));
        Assert.NotEqual(k, CacheStore.TextureKey(r, ImageKind.Exr, 101, 5));
    }

    [Fact] // [EDGE-synced-delivery-22]
    public void WriteAtomic_TokenCancelledBeforeMove_NoFinalFile()
    {
        using var dir = new TempDir();
        var store = new CacheStore(dir.File("c"));
        var target = System.IO.Path.Combine(store.Root, "sky_x.atex");
        using var cts = new System.Threading.CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => store.WriteAtomic(target, s => { s.Write(new byte[10]); cts.Cancel(); }, cts.Token));
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.EnumerateFiles(store.Root));
    }
}
