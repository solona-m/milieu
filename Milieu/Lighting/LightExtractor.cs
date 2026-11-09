using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Milieu.Lighting;

/// <summary>
/// Finds the light in a <see cref="RadianceProbe"/> (deterministic, pure). Image space: s = column, t = row, both in
/// [0,1]; longitude = (s − 0.5)·2π, latitude = (0.5 − t)·π, direction (cos lat cos lon, sin lat, cos lat sin lon).
/// <list type="number">
/// <item>Key: the texel with the highest luminance Y (ties: first in row-major order); its region is every texel within
/// 6° with Y ≥ 10% of the peak. It is a key only if the peak is ≥ 10x the median Y of the covered texels (a band
/// panorama's empty rows don't count) and either its energy is ≥ 5% of the total, or (a small, very strong sun) the
/// median is above 0, the peak is ≥ 100x it and the energy is ≥ 1% of the total.</item>
/// <item>Fills: the key region is zeroed, then a depth-2 median cut (Debevec) splits the rest into 4 rectangles along the
/// longer angular extent at the energy median; ranked by energy per solid angle.</item>
/// </list>
/// </summary>
public static class LightExtractor
{
    /// <summary>Bump when the extraction changes: it is part of the rig cache key.</summary>
    public const int Version = 2;

    public const double KeyRadiusDegrees = 6;
    public const float KeyRegionFraction = 0.1f;
    public const double KeyMinEnergyShare = 0.05;
    public const double KeyMinPeakOverMedian = 10;

    /// <summary>The strong-peak branch: a peak this many times a non-zero median is a key from <see cref="KeyStrongMinShare"/> up.</summary>
    public const double KeyStrongPeakOverMedian = 100;
    public const double KeyStrongMinShare = 0.01;
    public const int CutDepth = 2;

    private const int W = RadianceProbe.Width;
    private const int H = RadianceProbe.Height;

    public static double Longitude(double s) => (s - 0.5) * 2 * Math.PI;
    public static double Latitude(double t) => (0.5 - t) * Math.PI;

    public static Vector3 ImageDirection(double s, double t)
    {
        double lon = Longitude(s), lat = Latitude(t);
        return new Vector3((float)(Math.Cos(lat) * Math.Cos(lon)), (float)Math.Sin(lat), (float)(Math.Cos(lat) * Math.Sin(lon)));
    }

    /// <summary>The image coordinates of a direction (the inverse of <see cref="ImageDirection"/>); s wraps into [0,1).</summary>
    public static (float S, float T) ImageTexel(Vector3 dir)
    {
        var d = Vector3.Normalize(dir);
        var lon = Math.Atan2(d.Z, d.X);
        var s = lon / (2 * Math.PI) + 0.5;
        s -= Math.Floor(s);
        var t = 0.5 - Math.Asin(Math.Clamp(d.Y, -1f, 1f)) / Math.PI;
        return ((float)s, (float)Math.Clamp(t, 0, 1));
    }

    public static LightRig Extract(RadianceProbe probe)
    {
        var rgb = probe.Rgb;
        var n = W * H;
        var y = new double[n];
        var dOmega = new double[H];
        var dirs = new Vector3[n];
        for (var row = 0; row < H; row++)
        {
            var t = (row + 0.5) / H;
            dOmega[row] = 2 * Math.PI / W * (Math.PI / H) * Math.Cos(Latitude(t));
            for (var x = 0; x < W; x++)
            {
                var i = row * W + x;
                y[i] = Lum(rgb, i);
                dirs[i] = ImageDirection((x + 0.5) / W, t);
            }
        }

        var total = Vector3.Zero;
        var totalD = new double[3];
        for (var i = 0; i < n; i++)
        {
            var w = dOmega[i / W];
            totalD[0] += rgb[i * 3] * w; totalD[1] += rgb[i * 3 + 1] * w; totalD[2] += rgb[i * 3 + 2] * w;
        }
        total = new Vector3((float)totalD[0], (float)totalD[1], (float)totalD[2]);
        var totalY = LightRig.Luminance(total);

        // Peak: strictly greater, so the first texel in row-major order wins a tie.
        var peak = 0;
        for (var i = 1; i < n; i++) if (y[i] > y[peak]) peak = i;
        var yPeak = y[peak];

        var work = (float[])rgb.Clone();
        RigLight? key = null;
        if (yPeak > 0 && totalY > 0)
        {
            var cosR = Math.Cos(KeyRadiusDegrees * Math.PI / 180);
            var region = new List<int>();
            for (var i = 0; i < n; i++)
                if (Vector3.Dot(dirs[i], dirs[peak]) >= cosR && y[i] >= KeyRegionFraction * yPeak) region.Add(i);
            double er = 0, eg = 0, eb = 0;
            var sum = Vector3.Zero;
            foreach (var i in region)
            {
                var w = dOmega[i / W];
                er += rgb[i * 3] * w; eg += rgb[i * 3 + 1] * w; eb += rgb[i * 3 + 2] * w;
                sum += dirs[i] * (float)(y[i] * w);
            }
            var energy = new Vector3((float)er, (float)eg, (float)eb);
            var median = CoveredMedian(y, probe);
            var keyY = LightRig.Luminance(energy);
            var isKey = yPeak >= KeyMinPeakOverMedian * median
                        && (keyY >= KeyMinEnergyShare * totalY
                            || (median > 0 && yPeak >= KeyStrongPeakOverMedian * median && keyY >= KeyStrongMinShare * totalY));
            if (isKey)
            {
                var (s, t) = sum.Length() > 1e-12f ? ImageTexel(sum) : (((peak % W) + 0.5f) / W, ((peak / W) + 0.5f) / H);
                key = new RigLight(s, t, energy);
                foreach (var i in region) work[i * 3] = work[i * 3 + 1] = work[i * 3 + 2] = 0f;
            }
        }

        var regions = MedianCut(work, dOmega, dirs);
        return new LightRig(Version, key, regions, total);
    }

    private static double Lum(float[] rgb, int i) => 0.2126 * rgb[i * 3] + 0.7152 * rgb[i * 3 + 1] + 0.0722 * rgb[i * 3 + 2];

    /// <summary>The median luminance over the covered rows only (a band panorama's empty rows would pull it to 0).</summary>
    internal static double CoveredMedian(double[] y, RadianceProbe probe)
    {
        var top = Math.Clamp(probe.CoveredTop, 0, H);
        var rows = Math.Clamp(probe.CoveredRows, 0, H - top);
        if (rows == 0) return 0;
        var values = new double[rows * W];
        Array.Copy(y, top * W, values, 0, values.Length);
        Array.Sort(values);
        var m = values.Length / 2;
        return values.Length % 2 == 1 ? values[m] : (values[m - 1] + values[m]) / 2;
    }

    private readonly record struct Rect(int X0, int X1, int Y0, int Y1);

    /// <summary>The fills of a probe-sized linear RGB buffer (the median cut on its own, as the LDR estimate uses it).</summary>
    internal static RigLight[] MedianCut(float[] rgb)
    {
        var dOmega = new double[H];
        var dirs = new Vector3[W * H];
        for (var row = 0; row < H; row++)
        {
            var t = (row + 0.5) / H;
            dOmega[row] = 2 * Math.PI / W * (Math.PI / H) * Math.Cos(Latitude(t));
            for (var x = 0; x < W; x++) dirs[row * W + x] = ImageDirection((x + 0.5) / W, t);
        }
        return MedianCut(rgb, dOmega, dirs);
    }

    private static RigLight[] MedianCut(float[] rgb, double[] dOmega, Vector3[] dirs)
    {
        var rects = new List<Rect> { new(0, W, 0, H) };
        for (var depth = 0; depth < CutDepth; depth++)
        {
            var next = new List<Rect>();
            foreach (var r in rects)
            {
                var (a, b) = Split(r, rgb, dOmega);
                next.Add(a); next.Add(b);
            }
            rects = next;
        }

        var found = new List<(RigLight Light, double Radiance, int Index)>();
        for (var k = 0; k < rects.Count; k++)
        {
            var r = rects[k];
            double er = 0, eg = 0, eb = 0, omega = 0;
            var sum = Vector3.Zero;
            for (var row = r.Y0; row < r.Y1; row++)
            for (var x = r.X0; x < r.X1; x++)
            {
                var i = row * W + x;
                var w = dOmega[row];
                er += rgb[i * 3] * w; eg += rgb[i * 3 + 1] * w; eb += rgb[i * 3 + 2] * w;
                omega += w;
                sum += dirs[i] * (float)(Lum(rgb, i) * w);
            }
            var (s, t) = sum.Length() >= 1e-6f
                ? ImageTexel(sum)
                : ((float)((r.X0 + r.X1) / 2.0 / W), (float)((r.Y0 + r.Y1) / 2.0 / H));
            var energy = new Vector3((float)er, (float)eg, (float)eb);
            found.Add((new RigLight(s, t, energy), omega > 0 ? LightRig.Luminance(energy) / omega : 0, k));
        }
        return found.OrderByDescending(f => f.Radiance).ThenBy(f => f.Index).Select(f => f.Light).ToArray();
    }

    /// <summary>Splits along the longer angular extent (width scaled by the cosine of the centre latitude) at the energy median.</summary>
    private static (Rect, Rect) Split(Rect r, float[] rgb, double[] dOmega)
    {
        var centreLat = Latitude((r.Y0 + r.Y1) / 2.0 / H);
        var width = (r.X1 - r.X0) * (2 * Math.PI / W) * Math.Cos(centreLat);
        var height = (r.Y1 - r.Y0) * (Math.PI / H);
        var alongX = width > height * (1 + 1e-9);
        var (lo, hi) = alongX ? (r.X0, r.X1) : (r.Y0, r.Y1);
        if (hi - lo < 2) return alongX ? (r, r with { X0 = r.X1 }) : (r, r with { Y0 = r.Y1 });

        var sums = new double[hi - lo];
        for (var row = r.Y0; row < r.Y1; row++)
        for (var x = r.X0; x < r.X1; x++)
        {
            var e = Lum(rgb, row * W + x) * dOmega[row];
            sums[(alongX ? x : row) - lo] += e;
        }
        var total = sums.Sum();
        var cut = (lo + hi) / 2;
        if (total > 0)
        {
            double acc = 0;
            for (var k = 0; k < sums.Length; k++)
            {
                acc += sums[k];
                if (acc >= total / 2 * (1 - 1e-9)) { cut = lo + k + 1; break; }   // tolerance: float sums of equal halves
            }
            cut = Math.Clamp(cut, lo + 1, hi - 1);
        }
        return alongX ? (r with { X1 = cut }, r with { X0 = cut }) : (r with { Y1 = cut }, r with { Y0 = cut });
    }
}
