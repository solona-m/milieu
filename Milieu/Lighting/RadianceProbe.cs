using System;
using System.Threading;
using Milieu.Imaging;

namespace Milieu.Lighting;

/// <summary>
/// Where an 8-bit image is clipped (docs/adr/0010): a <see cref="Width"/> x <see cref="Height"/> grid in the same
/// <see cref="EquirectLayout"/> as the probe (8x finer, cells of 0.176°), each cell holding the area fraction of source
/// pixels that are clipped (<see cref="LdrLightEstimator.IsClipped"/>). A band panorama fills only its rows.
/// </summary>
public sealed class ClipMask
{
    public const int Width = 2048;
    public const int Height = 1024;

    /// <summary>Row-major, top row first, <see cref="Width"/> x <see cref="Height"/>, each in [0, 1].</summary>
    public required float[] Fraction { get; init; }

    public required int CoveredTop { get; init; }
    public required int CoveredRows { get; init; }

    /// <summary>
    /// The solid-angle share of the covered sphere whose source pixels are near-white clipped
    /// (<see cref="LdrLightEstimator.IsNearWhite"/>): what the saturated-image gate counts (never the warm-core branch).
    /// </summary>
    public required double WhiteShare { get; init; }
}

/// <summary>
/// A small radiance map of a panorama: 256x128 linear RGB, area-averaged from the source (box-upsampled when the
/// source is smaller), laid out like the texture (<see cref="EquirectLayout"/>): a horizon band fills only its rows,
/// recorded as <see cref="CoveredTop"/>/<see cref="CoveredRows"/>; the other rows stay 0. Float pixels are sanitised
/// first: NaN and negatives become 0, anything above 65504 (half-float max, +Inf included) becomes 65504. An 8-bit
/// image (<see cref="BuildSrgb8"/>) is linearised from sRGB and also gets a <see cref="ClipMask"/>.
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

    /// <summary>Where the 8-bit source was clipped; null for a float (HDR) probe.</summary>
    public ClipMask? Clip { get; init; }

    public static float Sanitise(float v) => float.IsNaN(v) || v <= 0f ? 0f : v > MaxValue ? MaxValue : v;

    /// <summary>sRGB 8-bit → linear, by the IEC 61966-2-1 curve.</summary>
    public static readonly float[] SrgbToLinear = BuildSrgbTable();

    private static float[] BuildSrgbTable()
    {
        var t = new float[256];
        for (var i = 0; i < 256; i++)
        {
            var c = i / 255.0;
            t[i] = (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
        }
        return t;
    }

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
            ProbeRow(rgb, (long)y * w * 3, xTaps, rows, (long)y * Width * 3);
        }
        return new RadianceProbe { Rgb = ProbeColumns(rows, place, yTaps), CoveredTop = place.BandTop, CoveredRows = place.BandHeight };
    }

    /// <summary>
    /// An 8-bit RGB image: one pass over the source rows builds both the linear probe (the same accumulation as
    /// <see cref="Build"/> on the linearised pixels) and the <see cref="ClipMask"/>.
    /// </summary>
    public static RadianceProbe BuildSrgb8(byte[] rgb, int w, int h, CancellationToken ct = default)
    {
        if (w <= 0 || h <= 0) throw new ArgumentOutOfRangeException(nameof(w));
        if (rgb.Length < (long)w * h * 3) throw new ArgumentException("Source buffer too small.", nameof(rgb));
        var place = EquirectLayout.Place(w, h, Width, Height);
        var xTaps = Taps(w, Width);
        var yTaps = Taps(h, place.BandHeight);
        var maskPlace = EquirectLayout.Place(w, h, ClipMask.Width, ClipMask.Height);
        var mxTaps = Taps(w, ClipMask.Width);
        var myTaps = Taps(h, maskPlace.BandHeight);
        var lut = SrgbToLinear;

        var rows = new float[(long)h * Width * 3];
        var maskRows = new float[(long)h * ClipMask.Width];
        var linear = new float[(long)w * 3];
        var clipped = new float[w];
        double whiteOmega = 0, coveredOmega = 0;
        for (var y = 0; y < h; y++)
        {
            if ((y & 63) == 0) ct.ThrowIfCancellationRequested();
            var src = (long)y * w * 3;
            var white = 0;
            for (var x = 0; x < w; x++)
            {
                var p = src + (long)x * 3;
                byte r = rgb[p], g = rgb[p + 1], b = rgb[p + 2];
                linear[x * 3] = lut[r]; linear[x * 3 + 1] = lut[g]; linear[x * 3 + 2] = lut[b];
                clipped[x] = LdrLightEstimator.IsClipped(r, g, b) ? 1f : 0f;
                if (LdrLightEstimator.IsNearWhite(r, g, b)) white++;
            }
            // This source row's latitude in the layout (a band sits in its rows), for the solid-angle weight.
            var t = (maskPlace.BandTop + (y + 0.5) * maskPlace.BandHeight / h) / ClipMask.Height;
            var weight = Math.Cos((0.5 - t) * Math.PI);
            whiteOmega += white * weight;
            coveredOmega += w * weight;
            ProbeRow(linear, 0, xTaps, rows, (long)y * Width * 3);
            var dst = (long)y * ClipMask.Width;
            for (var x = 0; x < ClipMask.Width; x++)
            {
                double f = 0;
                foreach (var (i, wt) in mxTaps[x]) f += clipped[i] * wt;
                maskRows[dst + x] = (float)f;
            }
        }
        ct.ThrowIfCancellationRequested();

        var fraction = new float[ClipMask.Width * ClipMask.Height];
        for (var y = 0; y < maskPlace.BandHeight; y++)
        {
            var o = (maskPlace.BandTop + y) * ClipMask.Width;
            foreach (var (i, wt) in myTaps[y])
            {
                var s = (long)i * ClipMask.Width;
                for (var k = 0; k < ClipMask.Width; k++) fraction[o + k] += (float)(maskRows[s + k] * wt);
            }
            for (var k = 0; k < ClipMask.Width; k++) fraction[o + k] = Math.Clamp(fraction[o + k], 0f, 1f);
        }
        return new RadianceProbe
        {
            Rgb = ProbeColumns(rows, place, yTaps), CoveredTop = place.BandTop, CoveredRows = place.BandHeight,
            Clip = new ClipMask
            {
                Fraction = fraction, CoveredTop = maskPlace.BandTop, CoveredRows = maskPlace.BandHeight,
                WhiteShare = coveredOmega > 0 ? whiteOmega / coveredOmega : 0,
            },
        };
    }

    /// <summary>One source row (at <paramref name="src"/>) → Width sanitised, area-weighted columns (at <paramref name="dst"/>).</summary>
    private static void ProbeRow(float[] rgb, long src, (int Index, double Weight)[][] xTaps, float[] rows, long dst)
    {
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

    /// <summary>The vertical pass: the band's rows from the horizontally filtered source rows, capped at MaxValue.</summary>
    private static float[] ProbeColumns(float[] rows, EquirectPlacement place, (int Index, double Weight)[][] yTaps)
    {
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
        return output;
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
