using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Milieu.Lighting;

/// <summary>What the estimate found, for the bundled-rig summary and tests (counts of components per filter).</summary>
/// <param name="Components">Seeded components of the clip mask.</param>
/// <param name="Specks">(a) Ω below <see cref="LdrLightEstimator.MinBlobSolidAngle"/>.</param>
/// <param name="Broad">(b) radius above <see cref="LdrLightEstimator.MaxBlobRadiusDegrees"/>.</param>
/// <param name="RejectedShare">(d) under <see cref="LdrLightEstimator.MinShareOfLargest"/> of the largest point blob.</param>
/// <param name="RejectedOutlier">(e) under <see cref="LdrLightEstimator.OutlierFactor"/> x the median point blob (0 when the fallback fired).</param>
/// <param name="OutlierFallback">(e) would have rejected every blob passing (d), and at most <see cref="LdrLightEstimator.FallbackMaxBlobs"/> did: (e) skipped.</param>
/// <param name="RejectedHalo">(f) no glow around it.</param>
/// <param name="Saturated">(c) more than <see cref="LdrLightEstimator.MaxClipShare"/> of the covered sphere strict-white clipped: no point sources.</param>
/// <param name="Kept">Blobs passing every filter (the top 1 + MaxFills of them are used).</param>
/// <param name="ClipShare">Clipped Ω (both branches of <see cref="LdrLightEstimator.IsClipped"/>) over covered Ω.</param>
/// <param name="StrictWhiteShare">Strict-white clipped Ω over covered Ω: what gate (c) uses (<see cref="ClipMask.StrictWhiteShare"/>).</param>
public sealed record EstimateStats(int Components, int Specks, int Broad, int RejectedShare, int RejectedOutlier, bool OutlierFallback,
                                   int RejectedHalo, bool Saturated, int Kept, double ClipShare, double StrictWhiteShare);

/// <summary>What became of one component (diagnostics and tests).</summary>
internal enum BlobVerdict { Speck, Broad, Saturated, Share, Outlier, Halo, Key, Lamp, Unused }

/// <summary>One seeded component of the clip mask: its direction (centroid), clipped Ω, radius and fate.</summary>
internal sealed record EstimatedBlob(Vector3 Direction, double SolidAngle, double RadiusDegrees, int FirstCell, BlobVerdict Verdict);

/// <summary>The rig, the counts and every component (in labelling order).</summary>
internal sealed record EstimateResult(LightRig Rig, EstimateStats Stats, IReadOnlyList<EstimatedBlob> Blobs);

/// <summary>
/// Estimates scene lighting from an 8-bit image (docs/adr/0010; deterministic, pure). LDR clips every bright source,
/// so direction comes from where the clipped pixels sit, colour from the unclipped glow around them, and brightness is
/// an assumption applied at plan time (<see cref="LightRigPlanner.Resolve"/>).
/// <list type="number">
/// <item>Label the probe's <see cref="ClipMask"/>: seeds ≥ <see cref="SeedFraction"/>, grown through 8-connected cells ≥
/// <see cref="GrowFraction"/>, wrapping horizontally. Per component: Ω = Σ fraction·dω, the centroid, and the radius (the
/// farthest member cell centre plus half a cell diagonal).</item>
/// <item>Filters: (a) speck, (b) broad; the rest are point blobs. (c) a saturated image keeps none. (d) Ω ≥ 5% of the
/// largest point blob. (e) with more than <see cref="ManyBlobs"/> point blobs, Ω ≥ 3x their median, unless that rejects
/// every blob passing (d) and at most <see cref="FallbackMaxBlobs"/> did (a room of similar lamps, not a star field):
/// then (e) is skipped. (f) the glow around it ≥ 1.25x the covered median Y (a zero median or an empty ring passes).</item>
/// <item>Ranked by Ω (ties: the lower first member cell, row-major): rank 0 is the key, the next
/// <see cref="LightRigLayout.MaxFills"/> are lamps, each coloured by its halo (luminance-weighted, luminance 1, saturation
/// capped at 0.5) and weighted Ω/Ω_key.</item>
/// <item>The key's and lamps' probe texels are zeroed and the rest median-cut into the fill regions; TotalEnergy is
/// their sum.</item>
/// </list>
/// Any change to a constant or the algorithm means bumping <see cref="Version"/> and regenerating the bundled rigs.
/// </summary>
public static class LdrLightEstimator
{
    /// <summary>Bump when the estimate changes (it is in the estimated rig's cache key and file), and regenerate the bundled rigs.</summary>
    public const int Version = 2;

    /// <summary>A source pixel is clipped when min(R,G,B) is at least this (a white core)…</summary>
    public const int ClipLevel = 245;

    /// <summary>…or when R and G are at least this and B at least <see cref="WarmCoreMinFloor"/> (a bright yellow core, like a sunset sun).</summary>
    public const int WarmCoreMax = 250;

    public const int WarmCoreMinFloor = 160;

    /// <summary>
    /// The clip rule (the single definition, used by <see cref="RadianceProbe.BuildSrgb8"/>): min(R,G,B) ≥
    /// <see cref="ClipLevel"/> (white), or R ≥ <see cref="WarmCoreMax"/>, G ≥ <see cref="WarmCoreMax"/> and B ≥
    /// <see cref="WarmCoreMinFloor"/> (a yellow core). Pale sky, sand, skin and orange signage are not clipped; a cream
    /// wall in full sun is (a documented limit), and a warm lamp core with G under 250 is missed.
    /// </summary>
    public static bool IsClipped(byte r, byte g, byte b) =>
        IsStrictWhite(r, g, b) || (r >= WarmCoreMax && g >= WarmCoreMax && b >= WarmCoreMinFloor);

    /// <summary>The strict (white) clip: the only one the saturated-image gate (c) counts.</summary>
    public static bool IsStrictWhite(byte r, byte g, byte b) => r >= ClipLevel && g >= ClipLevel && b >= ClipLevel;

    public const float SeedFraction = 0.25f;
    public const float GrowFraction = 0.1f;
    public const double MinBlobSolidAngle = 1e-5;
    public const double MaxBlobRadiusDegrees = 12;
    public const double MaxClipShare = 0.25;
    public const double MinShareOfLargest = 0.05;
    public const int ManyBlobs = 8;
    public const double OutlierFactor = 3;

    /// <summary>The outlier fallback fires only when at most this many point blobs pass (d) (ND-5: a star field has hundreds).</summary>
    public const int FallbackMaxBlobs = 32;

    public const double HaloOverMedian = 1.25;
    public const double HaloDegrees = 3;
    public const float MaxHaloSaturation = 0.5f;

    private const int MW = ClipMask.Width;
    private const int MH = ClipMask.Height;
    private const int PW = RadianceProbe.Width;
    private const int PH = RadianceProbe.Height;

    /// <summary>Half a mask cell's diagonal, in degrees (cells are 180/1024° square at the equator).</summary>
    private static readonly double HalfCellDiagonalDegrees = 0.5 * Math.Sqrt(2) * 180.0 / MH;

    public static (LightRig Rig, EstimateStats Stats) Analyse(RadianceProbe probe)
    {
        var r = AnalyseDetailed(probe);
        return (r.Rig, r.Stats);
    }

    private sealed class Blob
    {
        public double Omega;
        public Vector3 Direction;
        public double Radius;
        public int FirstCell = int.MaxValue;
        public List<int> Cells = [];
        public BlobVerdict Verdict = BlobVerdict.Unused;
        public Vector3 Colour = Vector3.One;
    }

    internal static EstimateResult AnalyseDetailed(RadianceProbe probe)
    {
        var rgb = probe.Rgb;
        var mask = probe.Clip;

        // Probe geometry and luminance.
        var y = new double[PW * PH];
        var pOmega = new double[PH];
        var pDirs = new Vector3[PW * PH];
        for (var row = 0; row < PH; row++)
        {
            var t = (row + 0.5) / PH;
            pOmega[row] = 2 * Math.PI / PW * (Math.PI / PH) * Math.Cos(LightExtractor.Latitude(t));
            for (var x = 0; x < PW; x++)
            {
                var i = row * PW + x;
                y[i] = 0.2126 * rgb[i * 3] + 0.7152 * rgb[i * 3 + 1] + 0.0722 * rgb[i * 3 + 2];
                pDirs[i] = LightExtractor.ImageDirection((x + 0.5) / PW, t);
            }
        }
        var median = LightExtractor.CoveredMedian(y, probe);
        var pTop = Math.Clamp(probe.CoveredTop, 0, PH);
        var pRows = Math.Clamp(probe.CoveredRows, 0, PH - pTop);

        double clipOmega = 0, coveredOmega = 0;
        var blobs = mask == null ? new List<Blob>() : Label(mask, out clipOmega, out coveredOmega);
        var clipShare = coveredOmega > 0 ? clipOmega / coveredOmega : 0;
        // (c) counts only the strict-white clips (ClipMask.StrictWhiteShare), never the warm branch: a cream sky must not
        // switch a real sun off.
        var strictShare = mask?.StrictWhiteShare ?? 0;
        var saturated = strictShare > MaxClipShare;

        // (a), (b), (c)
        var points = new List<Blob>();
        int specks = 0, broad = 0;
        foreach (var b in blobs)
        {
            if (b.Omega < MinBlobSolidAngle) { b.Verdict = BlobVerdict.Speck; specks++; }
            else if (b.Radius > MaxBlobRadiusDegrees) { b.Verdict = BlobVerdict.Broad; broad++; }
            else if (saturated) b.Verdict = BlobVerdict.Saturated;
            else points.Add(b);
        }

        // (d) share of the largest point blob.
        var rejectedShare = 0;
        var afterShare = new List<Blob>();
        if (points.Count > 0)
        {
            var largest = points.Max(b => b.Omega);
            foreach (var b in points)
            {
                if (b.Omega >= MinShareOfLargest * largest) afterShare.Add(b);
                else { b.Verdict = BlobVerdict.Share; rejectedShare++; }
            }
        }

        // (e) outlier among many, with the fallback. Two counts: pointBlobs (passing (a)-(b)) decides "many";
        // afterShare.Count (passing (a)-(d)) is what FallbackMaxBlobs caps.
        var pointBlobs = points.Count;
        var rejectedOutlier = 0;
        var fallback = false;
        var afterOutlier = afterShare;
        if (pointBlobs > ManyBlobs)
        {
            var sorted = points.Select(b => b.Omega).Order().ToArray();
            var m = sorted.Length / 2;
            var medianOmega = sorted.Length % 2 == 1 ? sorted[m] : (sorted[m - 1] + sorted[m]) / 2;
            var passing = afterShare.Where(b => b.Omega >= OutlierFactor * medianOmega).ToList();
            if (passing.Count == 0 && afterShare.Count > 0 && afterShare.Count <= FallbackMaxBlobs) fallback = true;
            else
            {
                foreach (var b in afterShare.Except(passing)) { b.Verdict = BlobVerdict.Outlier; rejectedOutlier++; }
                afterOutlier = passing;
            }
        }

        // Probe texels holding each blob's member cells (for the halo's exclusion and the zeroing).
        HashSet<int> TexelsOf(Blob b)
        {
            var set = new HashSet<int>();
            foreach (var c in b.Cells) set.Add(TexelOf(c, mask!, probe));
            return set;
        }

        // (f) glow, computed with the halo colour.
        var rejectedHalo = 0;
        var kept = new List<(Blob Blob, HashSet<int> Texels)>();
        foreach (var b in afterOutlier)
        {
            var texels = TexelsOf(b);
            var (meanY, colour) = Halo(b, texels, rgb, y, pOmega, pDirs, pTop, pRows);
            b.Colour = colour;
            if (median > 0 && meanY is { } my && my < HaloOverMedian * median) { b.Verdict = BlobVerdict.Halo; rejectedHalo++; continue; }
            kept.Add((b, texels));
        }

        // Ranking.
        var ranked = kept.OrderByDescending(k => k.Blob.Omega).ThenBy(k => k.Blob.FirstCell).ToList();
        var used = ranked.Take(1 + LightRigLayout.MaxFills).ToList();
        var work = (float[])rgb.Clone();
        RigLight? key = null;
        var lamps = new List<RigLight>();
        for (var i = 0; i < used.Count; i++)
        {
            var (b, texels) = used[i];
            b.Verdict = i == 0 ? BlobVerdict.Key : BlobVerdict.Lamp;
            foreach (var t in texels) work[t * 3] = work[t * 3 + 1] = work[t * 3 + 2] = 0f;
            var (s, tt) = LightExtractor.ImageTexel(b.Direction);
            var weight = i == 0 ? 1f : (float)(b.Omega / used[0].Blob.Omega);
            var light = new RigLight(s, tt, b.Colour * weight);
            if (i == 0) key = light;
            else lamps.Add(light);
        }

        var regions = LightExtractor.MedianCut(work);
        var total = regions.Aggregate(Vector3.Zero, (a, r) => a + r.Energy);
        var rig = new LightRig(LightExtractor.Version, key, regions, total, lamps.ToArray(), Version);
        var stats = new EstimateStats(blobs.Count, specks, broad, rejectedShare, rejectedOutlier, fallback, rejectedHalo, saturated,
                                      kept.Count, clipShare, strictShare);
        return new EstimateResult(rig, stats,
                                  blobs.Select(b => new EstimatedBlob(b.Direction, b.Omega, b.Radius, b.FirstCell, b.Verdict)).ToList());
    }

    /// <summary>
    /// The ring (radius, radius + <see cref="HaloDegrees"/>] of covered probe texels around a blob, without the texels
    /// holding its member cells: its mean Y (null when empty) and its colour (Σ L·Y·dω / Σ Y·dω at luminance 1,
    /// saturation capped toward white; white when empty or dark).
    /// </summary>
    private static (double? MeanY, Vector3 Colour) Halo(Blob b, HashSet<int> exclude, float[] rgb, double[] y, double[] pOmega,
                                                        Vector3[] pDirs, int top, int rows)
    {
        var cosIn = Math.Cos(b.Radius * Math.PI / 180);
        var cosOut = Math.Cos(Math.Min(180, b.Radius + HaloDegrees) * Math.PI / 180);
        double sumY = 0, sumOmega = 0, r = 0, g = 0, bl = 0;
        for (var row = top; row < top + rows; row++)
        for (var x = 0; x < PW; x++)
        {
            var i = row * PW + x;
            var d = Vector3.Dot(pDirs[i], b.Direction);
            if (d >= cosIn || d < cosOut || exclude.Contains(i)) continue;
            var w = pOmega[row];
            var wy = y[i] * w;
            sumOmega += w;
            sumY += wy;
            r += rgb[i * 3] * wy; g += rgb[i * 3 + 1] * wy; bl += rgb[i * 3 + 2] * wy;
        }
        if (sumOmega <= 0) return (null, Vector3.One);
        var meanY = sumY / sumOmega;
        if (meanY <= 1e-6) return (meanY, Vector3.One);
        var c = new Vector3((float)(r / sumY), (float)(g / sumY), (float)(bl / sumY));
        var lum = LightRig.Luminance(c);
        if (!(lum > 1e-6f)) return (meanY, Vector3.One);
        return (meanY, CapSaturation(c / lum));
    }

    /// <summary>
    /// The probe texel holding a mask cell. Columns map 8:1; rows map through each layout's band (offset and scale), so a
    /// band panorama's edge mask rows land inside the probe's covered rows (identical to row / 8 for a full frame).
    /// </summary>
    internal static int TexelOf(int cell, ClipMask mask, RadianceProbe probe)
    {
        int cy = cell / MW, cx = cell % MW;
        var px = cx * PW / MW;
        var mRows = Math.Max(1, mask.CoveredRows);
        var pRows = Math.Max(1, probe.CoveredRows);
        var f = (cy - mask.CoveredTop + 0.5) / mRows;
        var py = probe.CoveredTop + (int)Math.Floor(f * pRows);
        py = Math.Clamp(py, probe.CoveredTop, probe.CoveredTop + pRows - 1);
        return Math.Clamp(py, 0, PH - 1) * PW + px;
    }

    /// <summary>
    /// Blends a luminance-1 colour toward white until its saturation S = (max − min)/max is at most
    /// <see cref="MaxHaloSaturation"/>: k = S / ((1 − S)·max − min + S), colour·k + white·(1 − k).
    /// </summary>
    internal static Vector3 CapSaturation(Vector3 c)
    {
        const float s = MaxHaloSaturation;
        var max = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        var min = MathF.Min(c.X, MathF.Min(c.Y, c.Z));
        if (!(max > 0) || (max - min) / max <= s) return c;
        var k = s / ((1 - s) * max - min + s);
        return c * k + Vector3.One * (1 - k);
    }

    /// <summary>Hysteresis labelling of the mask's covered rows (8-connected, wrapping horizontally, not at the poles).</summary>
    private static List<Blob> Label(ClipMask mask, out double clipOmega, out double coveredOmega)
    {
        var f = mask.Fraction;
        var top = Math.Clamp(mask.CoveredTop, 0, MH);
        var bottom = top + Math.Clamp(mask.CoveredRows, 0, MH - top);
        var cellOmega = new double[MH];
        var sinLat = new double[MH];
        var cosLat = new double[MH];
        var cosLon = new double[MW];
        var sinLon = new double[MW];
        for (var row = 0; row < MH; row++)
        {
            var lat = LightExtractor.Latitude((row + 0.5) / MH);
            cellOmega[row] = 2 * Math.PI / MW * (Math.PI / MH) * Math.Cos(lat);
            sinLat[row] = Math.Sin(lat);
            cosLat[row] = Math.Cos(lat);
        }
        for (var x = 0; x < MW; x++)
        {
            var lon = LightExtractor.Longitude((x + 0.5) / MW);
            cosLon[x] = Math.Cos(lon);
            sinLon[x] = Math.Sin(lon);
        }

        clipOmega = 0;
        coveredOmega = 0;
        for (var row = top; row < bottom; row++)
        {
            coveredOmega += cellOmega[row] * MW;
            double s = 0;
            for (var x = 0; x < MW; x++) s += f[row * MW + x];
            clipOmega += s * cellOmega[row];
        }

        var label = new bool[MW * MH];
        var blobs = new List<Blob>();
        var queue = new Queue<int>();
        for (var row = top; row < bottom; row++)
        for (var x = 0; x < MW; x++)
        {
            var start = row * MW + x;
            if (label[start] || f[start] < SeedFraction) continue;
            var blob = new Blob();
            label[start] = true;
            queue.Enqueue(start);
            double sx = 0, sy = 0, sz = 0;
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                blob.Cells.Add(c);
                if (c < blob.FirstCell) blob.FirstCell = c;
                int cy = c / MW, cx = c % MW;
                var w = f[c] * cellOmega[cy];
                blob.Omega += w;
                sx += w * cosLat[cy] * cosLon[cx]; sy += w * sinLat[cy]; sz += w * cosLat[cy] * sinLon[cx];
                for (var dy = -1; dy <= 1; dy++)
                {
                    var ny = cy + dy;
                    if (ny < top || ny >= bottom) continue;
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        var nx = (cx + dx + MW) % MW;
                        var n = ny * MW + nx;
                        if (label[n] || f[n] < GrowFraction) continue;
                        label[n] = true;
                        queue.Enqueue(n);
                    }
                }
            }
            var len = Math.Sqrt(sx * sx + sy * sy + sz * sz);
            double ux = sx / len, uy = sy / len, uz = sz / len;
            blob.Direction = new Vector3((float)ux, (float)uy, (float)uz);
            var minDot = 1.0;
            foreach (var c in blob.Cells)
            {
                int cy = c / MW, cx = c % MW;
                var d = ux * cosLat[cy] * cosLon[cx] + uy * sinLat[cy] + uz * cosLat[cy] * sinLon[cx];
                if (d < minDot) minDot = d;
            }
            blob.Radius = Math.Acos(Math.Clamp(minDot, -1, 1)) * 180 / Math.PI + HalfCellDiagonalDegrees;
            blobs.Add(blob);
        }
        return blobs;
    }
}
