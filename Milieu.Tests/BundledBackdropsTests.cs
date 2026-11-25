using System;
using System.IO;
using System.Linq;
using Milieu.Assets;
using Milieu.Backdrop;
using Milieu.Imaging;
using Milieu.Tests.Support;
using StbImageSharp;
using Xunit;
using Xunit.Abstractions;

namespace Milieu.Tests;

/// <summary>
/// Product-asset tests: they read the committed images in Milieu/DefaultBackdrops (the acknowledged exception to the
/// synthetic-fixture rule, docs/adr/0004).
/// </summary>
public class BundledBackdropsTests(ITestOutputHelper output)
{
    private static readonly string[] NewStems = ["autumn", "christmasstudio", "christmasstudio2", "snow"];

    [Fact] // [AC-hdri-backdrop-11] [AC-more-bundled-scenes-6] [EDGE-more-bundled-scenes-1]
    public void CommittedImages_ObeyLimitsAndShapes()
    {
        foreach (var pin in BackdropPins.All)
        {
            var path = Path.Combine(RepoPaths.DefaultBackdrops(), pin.Stem + ".jpeg");
            var head = File.ReadAllBytes(path).Take(3).ToArray();
            Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, head);
            var probe = ImageLoader.Probe(path);
            Assert.Equal((8192, 4096), (probe.Width, probe.Height));
            Assert.False(probe.Progressive, pin.Stem);
            Assert.True(DecodeBudget.PredictPeakBytes(probe, 8192) < DecodeBudget.BudgetBytes);
        }
    }

    [Fact, Trait("Speed", "Slow")] // [AC-hdri-backdrop-11] [EDGE-hdri-backdrop-60] [AC-more-bundled-scenes-6] [EDGE-more-bundled-scenes-13]
    public void CommittedImages_WrapAround()
    {
        foreach (var pin in BackdropPins.All)
        {
            using var s = File.OpenRead(Path.Combine(RepoPaths.DefaultBackdrops(), pin.Stem + ".jpeg"));
            var img = ImageResult.FromStream(s, ColorComponents.RedGreenBlue);
            Assert.False(SeamDetector.Measure(img.Data, img.Width, img.Height).NoWrap, pin.Stem);
        }
    }

    [Fact] // [AC-hdri-backdrop-11] [AC-more-bundled-scenes-7] [EDGE-more-bundled-scenes-15]
    public void Credits_ListsEveryPinnedStem()
    {
        var credits = File.ReadAllText(Path.Combine(RepoPaths.DefaultBackdrops(), "CREDITS.md"));
        foreach (var pin in BackdropPins.All) Assert.Contains(pin.Stem + ".jpeg", credits);
    }

    [Fact] // [AC-hdri-backdrop-11] [AC-more-bundled-scenes-1] [EDGE-more-bundled-scenes-12]
    public void Csproj_DoesNotShipDefaultBackdrops()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoPaths.Root(), "Milieu", "Milieu.csproj"));
        Assert.Contains(@"<None Remove=""DefaultBackdrops\**"" />", csproj);
        Assert.Contains(@"<Content Remove=""DefaultBackdrops\**"" />", csproj);
        var output = Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!;
        Assert.Empty(Directory.GetFiles(output, "*.jpeg", SearchOption.AllDirectories));
    }

    [ReferenceFileFact(ReferenceFiles.HdriSourceDir)] // [EDGE-hdri-backdrop-52]
    public void Lodge_IsByteIdenticalToSource_WhenSourcePresent()
    {
        var src = Path.Combine(ReferenceFiles.Path(ReferenceFiles.HdriSourceDir), "lodge.jpeg");
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(Path.Combine(RepoPaths.DefaultBackdrops(), "lodge.jpeg")));
    }

    [Fact] // [AC-more-bundled-scenes-6] [AC-more-bundled-scenes-7] [EDGE-more-bundled-scenes-15]
    public void NewCommittedJpegs_HaveNoApp1OrApp11Segments()
    {
        foreach (var stem in NewStems)
        {
            var b = File.ReadAllBytes(Path.Combine(RepoPaths.DefaultBackdrops(), stem + ".jpeg"));
            Assert.Equal(new byte[] { 0xFF, 0xD8 }, b[..2]);
            var i = 2;
            while (true)
            {
                Assert.True(i + 4 <= b.Length && b[i] == 0xFF, $"{stem}: bad marker at {i}");
                var marker = b[i + 1];
                if (marker == 0xDA) break;   // SOS: entropy-coded data follows
                Assert.NotEqual((byte)0xE1, marker);   // APP1: EXIF / XMP
                Assert.NotEqual((byte)0xEB, marker);   // APP11: JUMBF / C2PA
                i += 2 + (b[i + 2] << 8 | b[i + 3]);
            }
        }
    }

    [Fact] // [AC-more-bundled-scenes-7] [EDGE-more-bundled-scenes-15]
    public void Credits_RecordsNewScenesLicenceAndTag()
    {
        var credits = File.ReadAllText(Path.Combine(RepoPaths.DefaultBackdrops(), "CREDITS.md"));
        var intro = credits[..credits.IndexOf("| File |", StringComparison.Ordinal)];
        Assert.Contains("`" + BackdropPins.Tag + "`", intro);
        Assert.DoesNotContain("backdrops-v2", intro);
        Assert.Contains("2026-11-24", intro);
        Assert.Contains("lodge.jpeg is the source file, unchanged", intro);
        Assert.DoesNotContain("The processed files carry no EXIF metadata", intro);
        foreach (var stem in NewStems) Assert.Contains("| " + stem + ".jpeg |", credits);
    }

    // Tolerances: measured on 2026-11-24 (see each test), plus a margin of 0.5 levels (mean) and 4 levels (p99), rounded
    // up. Loosening them is a plan deviation, not a fix.
    [ReferenceFileFact(ReferenceFiles.HdriSourceDir), Trait("Speed", "Slow")] // [AC-more-bundled-scenes-6] [EDGE-more-bundled-scenes-1] [EDGE-more-bundled-scenes-2]
    public void ChristmasScenes_AreToneMappedSource_WhenSourcePresent()
    {
        var defaults = new BackdropSettings();
        foreach (var (source, stem) in new[] { ("Christmas Studio.hdr", "christmasstudio"), ("Christmas Studio 2.hdr", "christmasstudio2") })
        {
            ImageResultFloat hdr;
            using (var s = File.OpenRead(Path.Combine(ReferenceFiles.Path(ReferenceFiles.HdriSourceDir), source)))
                hdr = ImageResultFloat.FromStream(s, ColorComponents.RedGreenBlue);
            var expected = ToneMapper.ToSrgb8(hdr.Data, hdr.Width, hdr.Height, defaults.ExposureEv, defaults.ToneMap);
            AssertClose(stem, expected, 3, ChristmasMaxMean, ChristmasMaxP99);
        }
    }

    [ReferenceFileFact(ReferenceFiles.HdriSourceDir), Trait("Speed", "Slow")] // [AC-more-bundled-scenes-6]
    public void AutumnAndSnow_AreResampledSource_WhenSourcePresent()
    {
        foreach (var stem in new[] { "autumn", "snow" })
        {
            ImageResult src;
            using (var s = File.OpenRead(Path.Combine(ReferenceFiles.Path(ReferenceFiles.HdriSourceDir), stem + ".jpeg")))
                src = ImageResult.FromStream(s, ColorComponents.RedGreenBlue);
            var expected = EquirectResampler.Resample(src.Data, src.Width, src.Height, 8192, 4096);
            AssertClose(stem, expected, 4, ResampledMaxMean, ResampledMaxP99);
        }
    }

    // Measured: christmasstudio mean 1.134 p99 5, christmasstudio2 mean 0.963 p99 5 (JPEG q92 only).
    private const double ChristmasMaxMean = 1.7, ChristmasMaxP99 = 9;
    // Measured: autumn mean 1.710 p99 8, snow mean 0.747 p99 5.
    private const double ResampledMaxMean = 2.3, ResampledMaxP99 = 12;

    /// <summary>The committed JPEG against the expected 8192x4096 pixels: mean and 99th-percentile absolute difference
    /// per channel, in 8-bit levels.</summary>
    private void AssertClose(string stem, byte[] expected, int channels, double maxMean, double maxP99)
    {
        ImageResult img;
        using (var s = File.OpenRead(Path.Combine(RepoPaths.DefaultBackdrops(), stem + ".jpeg")))
            img = ImageResult.FromStream(s, ColorComponents.RedGreenBlue);
        Assert.Equal((8192, 4096), (img.Width, img.Height));
        var hist = new long[256];
        long sum = 0, n = (long)img.Width * img.Height;
        for (long p = 0; p < n; p++)
            for (var c = 0; c < 3; c++)
            {
                var d = Math.Abs(img.Data[p * 3 + c] - expected[p * channels + c]);
                hist[d]++; sum += d;
            }
        var mean = sum / (3.0 * n);
        long target = (long)Math.Ceiling(3 * n * 0.99), acc = 0;
        var p99 = 0;
        while ((acc += hist[p99]) < target) p99++;
        output.WriteLine($"{stem}: mean {mean:0.000}, p99 {p99}");
        Assert.True(mean <= maxMean, $"{stem}: mean {mean:0.000} > {maxMean}");
        Assert.True(p99 <= maxP99, $"{stem}: p99 {p99} > {maxP99}");
    }
}
