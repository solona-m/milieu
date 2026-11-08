using System;
using System.Numerics;

namespace Milieu.Imaging;

/// <summary>Placement of a non-wrapping strip (Sunset) when the prepare tool turns it into a ready 2:1 panorama.</summary>
public readonly record struct StripPlan(int ScaledWidth, int ScaledHeight, int HorizonRow, int Top, int OutWidth, int OutHeight);

/// <summary>Size rules shared by the plugin (output texture) and tools/PrepareBackdrops (committed images).</summary>
public static class BackdropSizing
{
    public const int MaxWidth = 8192;
    public const int MaxHeight = 4096;
    public const int MinOutputWidth = 1024;

    /// <summary>Texture width: the next power of two of the input width, clamped to [1024, max]; height is half.</summary>
    public static (int Width, int Height) OutputSize(int inputWidth, int maxWidth)
    {
        if (maxWidth is not (4096 or 8192)) maxWidth = 8192;
        var p = (int)Math.Min(BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, inputWidth)), int.MaxValue);
        var w = Math.Clamp(p, MinOutputWidth, maxWidth);
        return (w, w / 2);
    }

    /// <summary>
    /// The committed size of a bundled equirect: unchanged if it already fits 8192x4096 (copied byte for byte),
    /// exactly 8192x4096 when near 2:1, otherwise scaled to fit keeping its aspect.
    /// </summary>
    public static (int Width, int Height, bool Copy) TargetSize(int w, int h)
    {
        if (w <= MaxWidth && h <= MaxHeight) return (w, h, true);
        var shape = EquirectLayout.ShapeOf(w, h);
        if (shape == PanoramaShape.Full) return (MaxWidth, MaxHeight, false);
        if (shape == PanoramaShape.Band) return (MaxWidth, (int)Math.Round((double)MaxWidth * h / w), false);
        var scale = Math.Min((double)MaxWidth / w, (double)MaxHeight / h);
        return ((int)Math.Round(w * scale), (int)Math.Round(h * scale), false);
    }

    /// <summary>
    /// A strip is scaled to half the output width (so the mirror tile fills it exactly), mirror-tiled to the full width,
    /// and placed so its horizon row lands on the equator.
    /// </summary>
    public static StripPlan PlanStrip(int w, int h, double horizonFraction, int outW = MaxWidth, int outH = MaxHeight)
    {
        var sw = outW / 2;
        var sh = (int)Math.Round((double)sw * h / w);
        var horizon = (int)Math.Round(horizonFraction * sh);
        var top = outH / 2 - horizon;
        if (top < 0 || top + sh > outH) throw new ArgumentOutOfRangeException(nameof(horizonFraction), "The strip does not fit vertically.");
        return new StripPlan(sw, sh, horizon, top, outW, outH);
    }

    /// <summary>Appends the horizontal mirror of an image, so column 0 meets the last column: seamless by construction.</summary>
    public static byte[] MirrorTile(byte[] px, int w, int h, int channels)
    {
        var outW = w * 2;
        var result = new byte[(long)outW * h * channels];
        for (var y = 0; y < h; y++)
        {
            Buffer.BlockCopy(px, y * w * channels, result, y * outW * channels, w * channels);
            for (var x = 0; x < w; x++)
                Buffer.BlockCopy(px, (y * w + x) * channels, result, (y * outW + outW - 1 - x) * channels, channels);
        }
        return result;
    }
}
