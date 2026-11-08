using System;

namespace Milieu.Imaging;

/// <summary>
/// Predicts the peak memory a conversion needs, from the probe alone, so oversized images are refused before decoding.
/// Peak = max(decode, resample, encode). The per-pixel constants model StbImageSharp's allocations:
/// <list type="bullet">
/// <item>The whole file is read into memory first, so decode includes the file length.</item>
/// <item>JPEG baseline ≈ 9 B/px (component planes + RGB output + managed copy); progressive ≈ 12 B/px (coefficients kept).</item>
/// <item>PNG 8-bit = 2C + 6 B/px and 16-bit = 4C + 6 B/px for C file components (inflated + unfiltered + RGB + copy).</item>
/// <item>HDR = 24 B/px (float RGB + managed copy).</item>
/// </list>
/// If in-game measurements show these are low, raise them here; the tests pin the examples.
/// </summary>
public static class DecodeBudget
{
    /// <summary>1.5 GiB: above this an LDR image is refused.</summary>
    public const long BudgetBytes = 1_610_612_736;

    public static double DecodeBytesPerPixel(ProbeInfo p) => p.Kind switch
    {
        ImageKind.Hdr => 24,
        ImageKind.Jpeg => p.Progressive ? 12 : 9,
        ImageKind.Png => (p.BitsPerChannel > 8 ? 4.0 : 2.0) * Math.Clamp(p.Components, 1, 4) + 6,
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    public static long PredictPeakBytes(ProbeInfo p, int outWidth)
    {
        var px = (double)p.Width * p.Height;
        var outH = outWidth / 2.0;
        var outPx = outWidth * outH;

        var decode = DecodeBytesPerPixel(p) * px + p.FileBytes;
        var resample = p.Kind == ImageKind.Hdr
            ? 12 * px + 3 * px + 4 * outPx          // float source + RGB8 after tone mapping + RGBA output
            : 3 * px + 4 * outPx;                   // RGB source kept + RGBA output
        var encode = 4 * outPx * 4 / 3              // RGBA mip chain
                   + 4 * outPx                      // encoder working copy of the largest mip
                   + outPx * 4 / 3;                 // BC7 output (1 B/px) for every mip
        return (long)Math.Ceiling(Math.Max(decode, Math.Max(resample, encode)));
    }
}
