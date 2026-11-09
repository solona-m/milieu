using System.IO;
using Milieu.Imaging;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public class DecodeBudgetTests
{
    private const long FileBytes = 20_000_000;
    private const long Px16k = 16384L * 8192;

    private static ProbeInfo Jpeg(int w, int h, bool progressive, long file = FileBytes) =>
        new(ImageKind.Jpeg, w, h, 8, 3, progressive, file);

    [Fact] // [AC-hdri-backdrop-6] [EDGE-hdri-backdrop-55]
    public void PredictPeakBytes_PinnedExamples()
    {
        Assert.Equal(134_217_728L, Px16k);

        // 16384x8192 baseline JPEG with a 20 MB file: 9 B/px + file.
        var baseline = DecodeBudget.PredictPeakBytes(Jpeg(16384, 8192, false), 8192);
        Assert.Equal(1_227_959_552L, baseline);
        Assert.True(baseline <= DecodeBudget.BudgetBytes);

        // The same size progressive: 12 B/px + file, over the 1.5 GiB budget (without the file it would sit exactly on it).
        var progressive = DecodeBudget.PredictPeakBytes(Jpeg(16384, 8192, true), 8192);
        Assert.Equal(1_630_612_736L, progressive);
        Assert.True(progressive > DecodeBudget.BudgetBytes);

        Assert.True(DecodeBudget.PredictPeakBytes(Jpeg(21184, 3848, true), 8192) <= DecodeBudget.BudgetBytes);

        var rgba16 = new ProbeInfo(ImageKind.Png, 16384, 8192, 16, 4, false, FileBytes);
        Assert.Equal(22 * Px16k + FileBytes, DecodeBudget.PredictPeakBytes(rgba16, 8192));
        Assert.True(DecodeBudget.PredictPeakBytes(rgba16, 8192) > DecodeBudget.BudgetBytes);

        var rgb8 = new ProbeInfo(ImageKind.Png, 12000, 6000, 8, 3, false, FileBytes);
        Assert.Equal(12L * 12000 * 6000 + FileBytes, DecodeBudget.PredictPeakBytes(rgb8, 8192));
        Assert.True(DecodeBudget.PredictPeakBytes(rgb8, 8192) <= DecodeBudget.BudgetBytes);

        var hdr = new ProbeInfo(ImageKind.Hdr, 8192, 4096, 32, 3, false, FileBytes);
        Assert.Equal(24L * 8192 * 4096 + FileBytes, DecodeBudget.PredictPeakBytes(hdr, 8192));

        // An 8192x4096 baseline JPEG (the bundled backdrops): the encode phase dominates (~358 MB).
        var bundled = DecodeBudget.PredictPeakBytes(Jpeg(8192, 4096, false, 10_000_000), 8192);
        const double outPx = 8192.0 * 4096;
        Assert.Equal((long)System.Math.Ceiling(4 * outPx * 4 / 3 + 4 * outPx + outPx * 4 / 3), bundled);
        Assert.InRange(bundled, 300_000_000, 450_000_000);

        // A small image to a small output stays small.
        Assert.True(DecodeBudget.PredictPeakBytes(Jpeg(1024, 512, false, 100_000), 1024) < 20_000_000);
    }

    [Fact] // [AC-exr-support-4]
    public void Exr_DecodeArmPinned()
    {
        var exr = new ProbeInfo(ImageKind.Exr, 8192, 4096, 16, 3, false, FileBytes);
        Assert.Equal(12, DecodeBudget.DecodeBytesPerPixel(exr));
        Assert.Equal(3 * 64L * 1024 * 1024 + 2 * 1024 * 1024, Milieu.Imaging.Exr.ExrLimits.WorkingSetBytes);
        const double px = 8192.0 * 4096, outPx = 8192.0 * 4096;
        var decode = 12 * px + Milieu.Imaging.Exr.ExrLimits.WorkingSetBytes;
        var resample = 12 * px + 3 * px + 4 * outPx;
        var encode = 4 * outPx * 4 / 3 + 4 * outPx + outPx * 4 / 3;
        var expected = (long)System.Math.Ceiling(System.Math.Max(decode, System.Math.Max(resample, encode)));
        Assert.Equal(expected, DecodeBudget.PredictPeakBytes(exr, 8192));
        Assert.Equal(expected, DecodeBudget.PredictPeakBytes(exr with { FileBytes = 500_000_000 }, 8192));   // the file is streamed
        Assert.True(expected < DecodeBudget.PredictPeakBytes(exr with { Kind = ImageKind.Hdr }, 8192));   // below the .hdr path
    }

    [Fact] // [EDGE-hdri-backdrop-55]
    public void ProgressiveFixture_FileLengthPinned()
    {
        using var t = new TempDir();
        var path = t.File("prog.jpg");
        File.WriteAllBytes(path, SyntheticImages.JpegHeader(0xC2, 16384, 8192, padToBytes: (int)FileBytes));
        Assert.Equal(FileBytes, new FileInfo(path).Length);
        var probe = ImageLoader.Probe(path);
        Assert.True(probe.Progressive);
        Assert.Equal(FileBytes, probe.FileBytes);
        Assert.True(DecodeBudget.PredictPeakBytes(probe, 8192) > DecodeBudget.BudgetBytes);
    }
}
