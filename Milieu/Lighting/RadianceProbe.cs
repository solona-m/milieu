using System;
using System.Threading;
using Milieu.Imaging;

namespace Milieu.Lighting;

/// <summary>
/// A small radiance map of a float panorama: 256x128 linear RGB, area-averaged from the source (box-upsampled when the
/// source is smaller), laid out like the texture (<see cref="EquirectLayout"/>): a horizon band fills only its rows,
/// recorded as <see cref="CoveredTop"/>/<see cref="CoveredRows"/>; the other rows stay 0. Pixels are sanitised first:
/// NaN and negatives become 0, anything above 65504 (half-float max, +Inf included) becomes 65504.
/// </summary>
public sealed class RadianceProbe
{
    public const int Width = 256;
    public const int Height = 128;
    public const float MaxValue = 65504f;

    /// <summary>Row-major linear RGB, top row first, <see cref="Width"/> x <see cref="Height"/> x 3.</summary>
    public required float[] Rgb { get; init; }

    public required int CoveredTop { get; init; }
    public required int CoveredRows { get; init; }

    public static float Sanitise(float v) => float.IsNaN(v) || v <= 0f ? 0f : v > MaxValue ? MaxValue : v;

    public static RadianceProbe Build(float[] rgb, int w, int h, CancellationToken ct = default)
    {
        if (w <= 0 || h <= 0) throw new ArgumentOutOfRangeException(nameof(w));
        if (rgb.Length < (long)w * h * 3) throw new ArgumentException("Source buffer too small.", nameof(rgb));
        var place = EquirectLayout.Place(w, h, Width, Height);
        var xTaps = Taps(w, Width);
        var yTaps = Taps(h, place.BandHeight);

        // Horizontal pass: each source row → Width columns. The intervals start at column 0, so none crosses the seam.
        var rows = new float[(long)h * Width * 3];
        for (var y = 0; y < h; y++)
        {
            if ((y & 63) == 0) ct.ThrowIfCancellationRequested();
            var src = (long)y * w * 3;
            var dst = (long)y * Width * 3;
            for (var x = 0; x < Width; x++)
            {
                double r = 0, g = 0, b = 0;
                foreach (var (i, wt) in xTaps[x])
                {
                    var p = src + (long)i * 3;
                    r += Sanitise(rgb[p]) * wt; g += Sanitise(rgb[p + 1]) * wt; b += Sanitise(rgb[p + 2]) * wt;
                }
                rows[dst + x * 3] = (float)r; rows[dst + x * 3 + 1] = (float)g; rows[dst + x * 3 + 2] = (float)b;
            }
        }

        var output = new float[Width * Height * 3];
        for (var y = 0; y < place.BandHeight; y++)
        {
            var o = (place.BandTop + y) * Width * 3;
            foreach (var (i, wt) in yTaps[y])
            {
                var s = (long)i * Width * 3;
                for (var k = 0; k < Width * 3; k++) output[o + k] += (float)(rows[s + k] * wt);
            }
            for (var k = 0; k < Width * 3; k++) output[o + k] = Math.Min(output[o + k], MaxValue);
        }
        return new RadianceProbe { Rgb = output, CoveredTop = place.BandTop, CoveredRows = place.BandHeight };
    }

    /// <summary>Area weights: destination i covers source [i·scale, (i+1)·scale); a partial pixel counts by its overlap.</summary>
    private static (int Index, double Weight)[][] Taps(int src, int dst)
    {
        var taps = new (int, double)[dst][];
        var scale = (double)src / dst;
        for (var i = 0; i < dst; i++)
        {
            var start = i * scale;
            var end = start + scale;
            var first = (int)Math.Floor(start);
            var last = Math.Min(src - 1, (int)Math.Ceiling(end) - 1);
            var list = new System.Collections.Generic.List<(int, double)>();
            for (var s = first; s <= last; s++)
            {
                var cover = Math.Min(end, s + 1) - Math.Max(start, s);
                if (cover > 0) list.Add((s, cover / scale));
            }
            taps[i] = list.ToArray();
        }
        return taps;
    }
}
