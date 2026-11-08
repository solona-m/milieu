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

    [Fact] // [AC-hdri-backdrop-11]
    public void Pins_cover_exactly_the_committed_jpeg_stems()
    {
        var committed = Directory.GetFiles(RepoPaths.DefaultBackdrops(), "*.jpeg").Select(Path.GetFileNameWithoutExtension).OrderBy(s => s, StringComparer.Ordinal);
        Assert.Equal(committed, BackdropPins.All.Select(p => p.Stem).OrderBy(s => s, StringComparer.Ordinal));
        Assert.Equal(8, BackdropPins.Count);
        Assert.Equal("Moon", BackdropPins.Find("moon")!.DisplayName);
        Assert.All(BackdropPins.All, p => Assert.Equal(AtexWriter.Layout(8192, 4096).TotalLength, p.Bytes));
        Assert.Equal(357_914_496L, BackdropPins.TotalBytes);
        Assert.Equal("backdrops-v2", BackdropPins.Tag);
        Assert.All(BackdropPins.All, p => Assert.Matches("^[0-9a-f]{64}$", p.Sha256));
        Assert.Equal(BackdropPins.Count, BackdropPins.All.Select(p => p.Sha256).Distinct().Count());
        Assert.EndsWith("/blob/backdrops-v2/Milieu/DefaultBackdrops/CREDITS.md", BackdropPins.CreditsUrl);
        Assert.True(BackdropPins.TryParseId("bundled:moon", out var moon) && moon.Stem == "moon");
        Assert.False(BackdropPins.TryParseId("bundled:nope", out _));
        Assert.False(BackdropPins.TryParseId(@"C:\moon.atex", out _));
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
