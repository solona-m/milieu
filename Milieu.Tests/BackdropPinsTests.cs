using System;
using System.IO;
using System.Linq;
using Milieu.Assets;
using Milieu.GameFiles;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public sealed class BackdropPinsTests : IDisposable
{
    private readonly TempDir dir = new();

    public void Dispose() => dir.Dispose();

    [Fact] // [AC-hdri-backdrop-11] [AC-more-bundled-scenes-3]
    public void Pins_cover_exactly_the_committed_jpeg_stems()
    {
        var committed = Directory.GetFiles(RepoPaths.DefaultBackdrops(), "*.jpeg").Select(Path.GetFileNameWithoutExtension).OrderBy(s => s, StringComparer.Ordinal);
        Assert.Equal(committed, BackdropPins.All.Select(p => p.Stem).OrderBy(s => s, StringComparer.Ordinal));
        Assert.Equal(12, BackdropPins.Count);
        Assert.Equal("Moon", BackdropPins.Find("moon")!.DisplayName);
        Assert.All(BackdropPins.All, p => Assert.Equal(AtexWriter.Layout(8192, 4096).TotalLength, p.Bytes));
        Assert.Equal(536_871_744L, BackdropPins.TotalBytes);
        Assert.Equal("backdrops-v3", BackdropPins.Tag);
        Assert.All(BackdropPins.All, p => Assert.Matches("^[0-9a-f]{64}$", p.Sha256));
        Assert.Equal(BackdropPins.Count, BackdropPins.All.Select(p => p.Sha256).Distinct().Count());
        Assert.EndsWith("/blob/backdrops-v3/Milieu/DefaultBackdrops/CREDITS.md", BackdropPins.CreditsUrl);
        Assert.True(BackdropPins.TryParseId("bundled:moon", out var moon) && moon.Stem == "moon");
        Assert.True(BackdropPins.TryParseId("bundled:christmasstudio2", out var xmas2) && xmas2.DisplayName == "Christmas Studio 2");
        Assert.False(BackdropPins.TryParseId("bundled:nope", out _));
        Assert.False(BackdropPins.TryParseId(@"C:\moon.atex", out _));
    }

    [Fact] // [AC-more-bundled-scenes-4] [EDGE-more-bundled-scenes-4]
    public void OldPins_Unchanged()
    {
        // The backdrops-v2 pins: the old scenes keep their published bytes, so nobody re-downloads them.
        (string Stem, string Sha)[] v2 =
        [
            ("forest", "80b81b010da5c49db392db65b0837d94d952c69419d54f794424a8f33e0eaa29"),
            ("galaxy", "5212832130fcb3916f4a05420b9a5f4c002896b52c9ecfc300759976ab50ec4b"),
            ("livingroom", "708926c13a82ecc5c5ebd8a4c8c89a753d760798ff2f6f17e15ae55037226b31"),
            ("livingroom2", "8727593be0652bf33c7b0827b79374613cac7322b9e81c26448e7a4fde90708d"),
            ("lodge", "ec19e94f6a4964e9d752678d5f995988b9d175571e3c060c50b16fe13ff59572"),
            ("moon", "74d3deb3519573150760287fd876eed2fa88f8735778e11456515a353bd64f5f"),
            ("sunset", "f646b04d1a92e8436a3857991b60f3ff865acd5883832e61ba6f6670028372bb"),
            ("warehouse", "6644700b04f9237f0c1ca07b21e66388075c26a9b827d75092e6c72fac7f2c92"),
        ];
        foreach (var (stem, sha) in v2) Assert.Equal(sha, BackdropPins.Find(stem)!.Sha256);
    }

    [Fact] // [EDGE-more-bundled-scenes-3]
    public void Stems_HaveNoSpacesAndRemoteNameIsFileName()
    {
        Assert.All(BackdropPins.All, p =>
        {
            Assert.Matches("^[a-z0-9]+$", p.Stem);
            Assert.Equal(p.FileName, BackdropPins.RemoteName(p));
        });
    }

    [Fact] // [EDGE-hdri-backdrop-50]
    public void Verify_reports_missing_extra_size_sha_header_and_tag_problems()
    {
        Assert.Contains(BackdropPins.Verify(dir.Path, "backdrops-v9"), p => p.Contains("does not match the pinned tag"));
        var problems = BackdropPins.Verify(dir.Path, BackdropPins.Tag);
        Assert.Equal(BackdropPins.Count, problems.Count(p => p.EndsWith("is missing")));

        File.WriteAllBytes(Path.Combine(dir.Path, "extra.atex"), [1]);
        File.WriteAllBytes(Path.Combine(dir.Path, "forest.atex"), [1, 2, 3]);
        using (var fs = File.Create(Path.Combine(dir.Path, "galaxy.atex"))) fs.SetLength(44739312);                 // zero header
        using (var fs = File.Create(Path.Combine(dir.Path, "moon.atex")))
        {
            fs.Write(AtexWriter.ExpectedHeader(8192, 4096));
            fs.SetLength(44739312);                                                                                // right header, wrong sha
        }
        problems = BackdropPins.Verify(dir.Path, BackdropPins.Tag);
        Assert.Contains(problems, p => p.Contains("extra.atex is not pinned"));
        Assert.Contains(problems, p => p.StartsWith("forest.atex is 3 bytes"));
        Assert.Contains(problems, p => p.Contains("galaxy.atex has an unexpected header"));
        Assert.Contains(problems, p => p.StartsWith("moon.atex sha256"));
    }
}
