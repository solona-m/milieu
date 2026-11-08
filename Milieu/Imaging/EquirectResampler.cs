using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Milieu.Imaging;

/// <summary>
/// Resamples an 8-bit RGB panorama into an RGBA output. Separable filter: an area (box) filter when shrinking, bilinear
/// (tent) when enlarging. Horizontally the image wraps (column 0 meets the last column); vertically it clamps.
/// Rows are produced in parallel bands, each keeping a small window of horizontally filtered source rows, so memory
/// stays near the output size even for a 21184-pixel-wide source.
/// </summary>
public static class EquirectResampler
{
    private readonly record struct Tap(int Index, float Weight);

    /// <summary>Resample into the whole destination frame.</summary>
    public static byte[] Resample(byte[] rgb, int sw, int sh, int dw, int dh, CancellationToken ct = default)
    {
        var output = new byte[(long)dw * dh * 4];
        ResampleInto(rgb, sw, sh, output, dw, dh, 0, dh, ct);
        return output;
    }

    /// <summary>
    /// Resample using the panorama layout: full frame, stretched, or a horizon band whose outside rows are faded
    /// with <see cref="BandFill"/> (fade over outH/8 rows).
    /// </summary>
    public static byte[] ResampleLayout(byte[] rgb, int sw, int sh, int outW, int outH, CancellationToken ct = default)
    {
        var place = EquirectLayout.Place(sw, sh, outW, outH);
        var output = new byte[(long)outW * outH * 4];
        ResampleInto(rgb, sw, sh, output, outW, outH, place.BandTop, place.BandHeight, ct);
        if (place.Shape == PanoramaShape.Band)
            BandFill.Fill(output, outW, outH, 4, place.BandTop, place.BandHeight, Math.Max(1, outH / 8));
        return output;
    }

    /// <summary>Draws the source into rows [top, top+height) of an RGBA destination.</summary>
    public static void ResampleInto(byte[] rgb, int sw, int sh, byte[] dest, int dw, int dh, int top, int height, CancellationToken ct)
    {
        if (rgb.Length < (long)sw * sh * 3) throw new ArgumentException("Source buffer too small.", nameof(rgb));
        if (dest.Length < (long)dw * dh * 4) throw new ArgumentException("Destination buffer too small.", nameof(dest));
        var xTaps = Taps(sw, dw, wrap: true);
        var yTaps = Taps(sh, height, wrap: false);

        const int band = 64;
        var bands = (height + band - 1) / band;
        Parallel.For(0, bands, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = ToneMapper.Workers }, b =>
        {
            var cache = new Dictionary<int, float[]>();
            var y0 = b * band;
            var y1 = Math.Min(height, y0 + band);
            var acc = new float[dw * 3];
            for (var y = y0; y < y1; y++)
            {
                ct.ThrowIfCancellationRequested();
                Array.Clear(acc);
                foreach (var t in yTaps[y])
                {
                    if (!cache.TryGetValue(t.Index, out var row))
                    {
                        row = FilterRow(rgb, sw, t.Index, xTaps, dw);
                        cache[t.Index] = row;
                    }
                    for (var i = 0; i < acc.Length; i++) acc[i] += row[i] * t.Weight;
                }
                // Drop rows the next output row can no longer need.
                if (y + 1 < y1)
                {
                    var minNext = int.MaxValue;
                    foreach (var t in yTaps[y + 1]) minNext = Math.Min(minNext, t.Index);
                    var stale = new List<int>();
                    foreach (var k in cache.Keys) if (k < minNext) stale.Add(k);
                    foreach (var k in stale) cache.Remove(k);
                }
                var o = (long)(top + y) * dw * 4;
                for (var x = 0; x < dw; x++)
                {
                    dest[o + x * 4] = ToByte(acc[x * 3]);
                    dest[o + x * 4 + 1] = ToByte(acc[x * 3 + 1]);
                    dest[o + x * 4 + 2] = ToByte(acc[x * 3 + 2]);
                    dest[o + x * 4 + 3] = 255;
                }
            }
        });
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)(v + 0.5f), 0, 255);

    private static float[] FilterRow(byte[] rgb, int sw, int sy, Tap[][] xTaps, int dw)
    {
        var row = new float[dw * 3];
        var src = (long)sy * sw * 3;
        for (var x = 0; x < dw; x++)
        {
            float r = 0, g = 0, b = 0;
            foreach (var t in xTaps[x])
            {
                var p = src + (long)t.Index * 3;
                r += rgb[p] * t.Weight; g += rgb[p + 1] * t.Weight; b += rgb[p + 2] * t.Weight;
            }
            row[x * 3] = r; row[x * 3 + 1] = g; row[x * 3 + 2] = b;
        }
        return row;
    }

    /// <summary>
    /// Filter taps for each destination sample. Shrinking: each output covers <c>scale</c> source pixels (area weights,
    /// partial pixels at the ends). Enlarging: bilinear between the two nearest source centres.
    /// </summary>
    private static Tap[][] Taps(int src, int dst, bool wrap)
    {
        var taps = new Tap[dst][];
        var scale = (double)src / dst;
        for (var i = 0; i < dst; i++)
        {
            var list = new List<Tap>();
            if (scale > 1)
            {
                var start = i * scale;
                var end = start + scale;
                for (var s = (int)Math.Floor(start); s < (int)Math.Ceiling(end); s++)
                {
                    var cover = Math.Min(end, s + 1) - Math.Max(start, s);
                    if (cover <= 0) continue;
                    list.Add(new Tap(Clamp(s, src), (float)(cover / scale)));
                }
            }
            else
            {
                var centre = (i + 0.5) * scale - 0.5;
                var s0 = (int)Math.Floor(centre);
                var f = centre - s0;
                var a = wrap ? Wrap(s0, src) : Math.Clamp(s0, 0, src - 1);
                var b = wrap ? Wrap(s0 + 1, src) : Math.Clamp(s0 + 1, 0, src - 1);
                list.Add(new Tap(a, (float)(1 - f)));
                if (f > 0) list.Add(new Tap(b, (float)f));
            }
            taps[i] = Merge(list);
        }
        return taps;
    }

    private static int Clamp(int s, int n) => Math.Clamp(s, 0, n - 1);

    private static int Wrap(int s, int n) => ((s % n) + n) % n;

    private static Tap[] Merge(List<Tap> list)
    {
        var d = new Dictionary<int, float>();
        foreach (var t in list) d[t.Index] = d.GetValueOrDefault(t.Index) + t.Weight;
        var result = new Tap[d.Count];
        var k = 0;
        foreach (var (idx, w) in d) result[k++] = new Tap(idx, w);
        Array.Sort(result, (p, q) => p.Index.CompareTo(q.Index));
        return result;
    }
}
