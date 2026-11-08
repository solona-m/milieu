using System;
using System.IO;
using System.Linq;
using Milieu.Backdrop;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public sealed class ThumbStoreTests : IDisposable
{
    private readonly TempDir dir = new();

    public void Dispose() => dir.Dispose();

    [Fact] // [AC-hdri-backdrop-14]
    public void KeyFromPathLengthMtime_SaveLoadRoundTrip_131072Bytes()
    {
        var store = new ThumbStore(dir.File("thumbs"));
        var img = dir.File("pano.jpg");
        File.WriteAllBytes(img, [1, 2, 3]);
        var key = ThumbStore.Key(img);
        Assert.Matches("^[0-9a-f]{16}$", key);
        Assert.Null(store.TryLoad(img));
        var rgba = Enumerable.Range(0, ThumbStore.Bytes).Select(i => (byte)i).ToArray();
        Assert.Equal(131_072, rgba.Length);
        store.Save(img, rgba);
        Assert.Equal(rgba, store.TryLoad(img));

        File.WriteAllBytes(img, [1, 2, 3, 4]);   // edited: a new key, the old thumbnail no longer applies
        Assert.NotEqual(key, ThumbStore.Key(img));
        Assert.Null(store.TryLoad(img));
        Assert.Null(ThumbStore.Key(dir.File("missing.jpg")));
        Assert.Throws<ArgumentException>(() => store.Save(img, new byte[10]));
    }

    [Fact] // [EDGE-hdri-backdrop-80]
    public void Cleanup_OnlyOwnPatternNotInUserImages()
    {
        var root = dir.File("thumbs");
        var store = new ThumbStore(root);
        var keep = dir.File("keep.jpg");
        var gone = dir.File("gone.jpg");
        File.WriteAllBytes(keep, [1]);
        File.WriteAllBytes(gone, [2]);
        store.Save(keep, new byte[ThumbStore.Bytes]);
        store.Save(gone, new byte[ThumbStore.Bytes]);
        File.WriteAllBytes(Path.Combine(root, "notes.txt"), [1]);
        Assert.Equal(1, store.Cleanup([keep]));
        Assert.NotNull(store.TryLoad(keep));
        Assert.Null(store.TryLoad(gone));
        Assert.True(File.Exists(Path.Combine(root, "notes.txt")));
    }
}
