using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Milieu.Backdrop;
using Milieu.Lighting;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public sealed class LightRigStoreTests : IDisposable
{
    private readonly TempDir dir = new();
    private readonly CacheStore cache;
    private readonly LightRigStore store;

    public LightRigStoreTests()
    {
        cache = new CacheStore(dir.File("cache"));
        store = new LightRigStore(cache);
    }

    public void Dispose() => dir.Dispose();

    private static LightRig Sample(bool key = true) =>
        new(LightExtractor.Version, key ? new RigLight(0.25f, 0.125f, new Vector3(1.5f, 2.25f, 3f)) : null,
            [new(0f, 0.5f, new Vector3(1, 2, 3)), new(1f, 1f, Vector3.Zero), new(0.5f, 0f, new Vector3(0.1f, 0.2f, 0.3f)), new(0.75f, 0.6f, Vector3.One)],
            new Vector3(9, 8, 7));

    [Fact] // [EDGE-scene-lighting-24]
    public void RoundTrip()
    {
        foreach (var rig in new[] { Sample(), Sample(key: false) })
        {
            store.Save("0123456789abcdef", rig);
            var back = store.Load("0123456789abcdef");
            Assert.NotNull(back);
            Assert.Equal(rig.Version, back!.Version);
            Assert.Equal(rig.Key, back.Key);
            Assert.Equal(rig.Regions, back.Regions);
            Assert.Equal(rig.TotalEnergy, back.TotalEnergy);
        }
        Assert.Equal(Path.Combine(cache.Root, "lights", "lights_0123456789abcdef.json"), cache.LightRigPath("0123456789abcdef"));
        Assert.Empty(Directory.GetFiles(cache.LightRigDir, "*.tmp"));
        Assert.Null(store.Load("ffffffffffffffff"));   // absent: a miss, nothing thrown
    }

    public static TheoryData<string> BadFiles => new()
    {
        "not json at all",
        "[]",
        "{}",
        $"{{\"version\":{LightExtractor.Version + 1},\"key\":null,\"regions\":[{R},{R},{R},{R}],\"total\":{T}}}",
        $"{{\"version\":{LightExtractor.Version},\"key\":null,\"regions\":[{R},{R},{R}],\"total\":{T}}}",
        $"{{\"version\":{LightExtractor.Version},\"key\":null,\"regions\":[{R},{R},{R},{R},{R}],\"total\":{T}}}",
        $"{{\"version\":{LightExtractor.Version},\"key\":{{\"s\":1.5,\"t\":0.5,\"r\":1,\"g\":1,\"b\":1}},\"regions\":[{R},{R},{R},{R}],\"total\":{T}}}",
        $"{{\"version\":{LightExtractor.Version},\"key\":{{\"s\":0.5,\"t\":0.5,\"r\":-1,\"g\":1,\"b\":1}},\"regions\":[{R},{R},{R},{R}],\"total\":{T}}}",
        $"{{\"version\":{LightExtractor.Version},\"key\":{{\"s\":0.5,\"t\":0.5,\"r\":\"NaN\",\"g\":1,\"b\":1}},\"regions\":[{R},{R},{R},{R}],\"total\":{T}}}",
        $"{{\"version\":{LightExtractor.Version},\"key\":null,\"regions\":[{R},{R},{R},{R}],\"total\":{{\"r\":1e999,\"g\":1,\"b\":1}}}}",
        $"{{\"version\":{LightExtractor.Version},\"key\":null,\"regions\":[{R},{R},{R},{R}]}}",
        $"{{\"version\":{LightExtractor.Version},\"key\":null,\"regions\":[{R},{R},{R},null],\"total\":{T}}}",
    };

    private const string R = "{\"s\":0.5,\"t\":0.5,\"r\":1,\"g\":2,\"b\":3}";
    private const string T = "{\"r\":4,\"g\":5,\"b\":6}";

    [Theory] // [EDGE-scene-lighting-24]
    [MemberData(nameof(BadFiles))]
    public void Corrupt_WrongVersion_NonFinite_WrongCount_AreMissesAndDeleted(string text)
    {
        Directory.CreateDirectory(cache.LightRigDir);
        var path = cache.LightRigPath("00000000000000aa");
        File.WriteAllText(path, text);
        Assert.Null(store.Load("00000000000000aa"));
        Assert.False(File.Exists(path));
    }

    [Fact] // [EDGE-scene-lighting-24]
    public void ValidHandWrittenFile_Loads()
    {
        Directory.CreateDirectory(cache.LightRigDir);
        File.WriteAllText(cache.LightRigPath("00000000000000bb"),
            $"{{\"version\":{LightExtractor.Version},\"key\":null,\"regions\":[{R},{R},{R},{R}],\"total\":{T}}}");
        var rig = store.Load("00000000000000bb");
        Assert.NotNull(rig);
        Assert.Null(rig!.Key);
        Assert.Equal(new Vector3(4, 5, 6), rig.TotalEnergy);
    }

    [Fact] // [EDGE-scene-lighting-31]
    public void TrimKeepsNewest100_OnlyOwnPattern()
    {
        Directory.CreateDirectory(cache.LightRigDir);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 105; i++)
        {
            var p = cache.LightRigPath(i.ToString("x16"));
            File.WriteAllText(p, "{}");
            File.SetLastWriteTimeUtc(p, start.AddMinutes(i));
        }
        var other = Path.Combine(cache.LightRigDir, "notes.json");
        var stray = Path.Combine(cache.LightRigDir, "lights_x.txt");
        var top = Path.Combine(cache.Root, "lights_0000000000000000.json");
        File.WriteAllText(other, "x");
        File.WriteAllText(stray, "x");
        File.WriteAllText(top, "x");

        Assert.Equal(5, store.TrimRigs(100));
        var left = Directory.GetFiles(cache.LightRigDir, "lights_*.json").Select(Path.GetFileName).ToHashSet();
        Assert.Equal(100, left.Count);
        for (var i = 0; i < 5; i++) Assert.DoesNotContain($"lights_{i:x16}.json", left);   // the oldest five
        Assert.Contains($"lights_{104:x16}.json", left);
        Assert.True(File.Exists(other) && File.Exists(stray) && File.Exists(top));
        Assert.Equal(0, new LightRigStore(new CacheStore(dir.File("absent"))).TrimRigs());
    }
}
