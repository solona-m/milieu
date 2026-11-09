using System;
using System.Linq;
using System.Numerics;
using Milieu.Lighting;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

/// <summary>
/// The LDR light estimate (docs/adr/0010) on synthetic 8-bit panoramas (2048x1024 unless stated). Every case also checks
/// the energy invariants: Σ regions = TotalEnergy, and after <see cref="LightRigPlanner.Resolve"/> regions + key + lamps
/// = total.
/// </summary>
public class LdrLightEstimatorTests
{
    private const int W = 2048, H = 1024;

    private static (LightRig Rig, EstimateStats Stats) Analyse(byte[] px, int w = W, int h = H)
    {
        var (rig, stats) = LdrLightEstimator.Analyse(RadianceProbe.BuildSrgb8(px, w, h));
        AssertEnergy(rig);
        return (rig, stats);
    }

    private static EstimateResult Detailed(byte[] px, int w = W, int h = H)
    {
        var r = LdrLightEstimator.AnalyseDetailed(RadianceProbe.BuildSrgb8(px, w, h));
        AssertEnergy(r.Rig);
        return r;
    }

    private static void AssertEnergy(LightRig rig)
    {
        Assert.True(rig.IsEstimated);
        Assert.Equal(LdrLightEstimator.Version, rig.EstimatorVersion);
        Assert.Equal(LightExtractor.Version, rig.Version);
        Assert.Equal(LightRig.RegionCount, rig.Regions.Length);
        Assert.InRange(rig.Lamps.Length, 0, LightRig.MaxLamps);
        if (rig.Key == null) Assert.Empty(rig.Lamps);
        var sum = rig.Regions.Aggregate(Vector3.Zero, (a, r) => a + r.Energy);
        Assert.True(Vector3.Distance(sum, rig.TotalEnergy) <= 1e-4f * Math.Max(rig.TotalEnergy.Length(), 1e-6f), "Σ regions = total");
        var resolved = LightRigPlanner.Resolve(rig);
        var all = resolved.Regions.Concat(resolved.Lamps).Aggregate(resolved.Key?.Energy ?? Vector3.Zero, (a, r) => a + r.Energy);
        Assert.True(Vector3.Distance(all, resolved.TotalEnergy) <= 1e-5f * Math.Max(resolved.TotalEnergy.Length(), 1e-6f),
                    "resolved: regions + key + lamps = total");
    }

    private static Vector3 DirOf(RigLight l)
    {
        var (az, el) = SyntheticImages.AzEl(l.S, l.T);
        return SyntheticImages.Dir(az, el);
    }

    private static double Off(RigLight l, double az, double el) => SyntheticImages.AngleDeg(DirOf(l), SyntheticImages.Dir(az, el));

    private static float Y(Vector3 e) => LightRig.Luminance(e);

    private static double Linear(int v)
    {
        var c = v / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>The expected halo colour of a uniform glow: linear, luminance 1, saturation capped at 0.5 toward white.</summary>
    private static Vector3 ExpectedHalo(byte r, byte g, byte b)
    {
        var c = new Vector3((float)Linear(r), (float)Linear(g), (float)Linear(b));
        c /= Y(c);
        var max = Math.Max(c.X, Math.Max(c.Y, c.Z));
        var min = Math.Min(c.X, Math.Min(c.Y, c.Z));
        const float s = LdrLightEstimator.MaxHaloSaturation;
        if ((max - min) / max > s)
        {
            var k = s / ((1 - s) * max - min + s);
            c = c * k + Vector3.One * (1 - k);
        }
        return c;
    }

    private static float Saturation(Vector3 c) => (Math.Max(c.X, Math.Max(c.Y, c.Z)) - Math.Min(c.X, Math.Min(c.Y, c.Z))) / Math.Max(c.X, Math.Max(c.Y, c.Z));

    private static readonly (byte, byte, byte) BlueSky = (90, 140, 220);
    private static readonly (byte, byte, byte) DimRoom = (40, 35, 30);

    // ---- Acceptance criteria ----

    [Fact] // [AC-ldr-light-estimate-1]
    public void ClippedSunDisc_KeyDirectionAndHaloColour()
    {
        var px = SyntheticImages.LdrPanorama(W, H, (_, _) => BlueSky);
        SyntheticImages.Lamp(px, W, H, 40, 30, 2, (255, 200, 120), glowDeg: 6);
        var (rig, stats) = Analyse(px);
        var key = Assert.IsType<RigLight>(rig.Key);
        Assert.True(Off(key, 40, 30) < 1.5, "key direction");
        Assert.Empty(rig.Lamps);
        Assert.Equal(1, stats.Kept);

        var expected = ExpectedHalo(255, 200, 120);
        Assert.Equal(1f, Y(key.Energy), 1e-3f);
        Assert.True(Math.Abs(key.Energy.X - expected.X) <= 0.05f * expected.X, $"R {key.Energy.X} vs {expected.X}");
        Assert.True(Math.Abs(key.Energy.Y - expected.Y) <= 0.05f * expected.Y, $"G {key.Energy.Y} vs {expected.Y}");
        Assert.True(Math.Abs(key.Energy.Z - expected.Z) <= 0.05f * expected.Z, $"B {key.Energy.Z} vs {expected.Z}");

        var resolved = LightRigPlanner.Resolve(rig);
        var sky = Math.Max(Y(rig.TotalEnergy), LightRigLayout.EstimatedSkyFloorY);
        Assert.Equal(LightRigLayout.EstimatedKeyOverSky * sky, Y(resolved.Key!.Energy), 0.01f * LightRigLayout.EstimatedKeyOverSky * sky);
    }

    [Fact] // [AC-ldr-light-estimate-3]
    public void SeveralLamps_EachOwnDirectionAndColour()
    {
        var px = SyntheticImages.LdrPanorama(W, H, (_, _) => DimRoom);
        SyntheticImages.Lamp(px, W, H, -120, 20, 1.0, (255, 180, 100));   // warm
        SyntheticImages.Lamp(px, W, H, 0, 20, 0.8, (150, 200, 255));      // cool
        SyntheticImages.Lamp(px, W, H, 120, 20, 0.6, (170, 255, 150));    // green
        var (rig, stats) = Analyse(px);
        Assert.Equal(3, stats.Kept);
        var key = Assert.IsType<RigLight>(rig.Key);
        Assert.True(Off(key, -120, 20) < 1, "the largest lamp is the key");
        Assert.Equal(2, rig.Lamps.Length);
        Assert.True(Off(rig.Lamps[0], 0, 20) < 1, "the 0.8° lamp");
        Assert.True(Off(rig.Lamps[1], 120, 20) < 1, "the 0.6° lamp");
        Assert.True(key.Energy.X > key.Energy.Y && key.Energy.Y > key.Energy.Z, "warm key");
        Assert.True(rig.Lamps[0].Energy.Z > rig.Lamps[0].Energy.Y && rig.Lamps[0].Energy.Y > rig.Lamps[0].Energy.X, "cool lamp");
        Assert.True(rig.Lamps[1].Energy.Y > rig.Lamps[1].Energy.X && rig.Lamps[1].Energy.Y > rig.Lamps[1].Energy.Z, "green lamp");
        // Relative weight Ω_i / Ω_key on a colour of luminance 1: about (0.8/1.0)² and (0.6/1.0)².
        Assert.Equal(1f, Y(key.Energy), 1e-3f);
        Assert.Equal(0.64f, Y(rig.Lamps[0].Energy), 0.64f * 0.2f);
        Assert.Equal(0.36f, Y(rig.Lamps[1].Energy), 0.36f * 0.25f);
    }

    [Fact] // [AC-ldr-light-estimate-3] [EDGE-ldr-light-estimate-41]
    public void SmallestLamp_0p3Degree_WorstAlignment_Detected()
    {
        // 4096x2048: a mask cell is 2x2 pixels. The disc is centred on a cell corner (pixel edge 2400, 700), so it splits
        // over four cells, the worst case for the seed threshold.
        const int w = 4096, h = 2048;
        var (az, el) = SyntheticImages.PixelAzEl(2400, 700, w, h);
        var px = SyntheticImages.LdrPanorama(w, h, (_, _) => DimRoom);
        SyntheticImages.Lamp(px, w, h, az, el, 0.3, (255, 190, 120));
        var (rig, stats) = Analyse(px, w, h);
        Assert.Equal(0, stats.Specks);
        var key = Assert.IsType<RigLight>(rig.Key);
        Assert.True(Off(key, az, el) < 0.5, "the 0.3° lamp is found");
    }

    [Fact] // [AC-ldr-light-estimate-3] [EDGE-ldr-light-estimate-42]
    public void TwelveDownlights_OutlierFallback_KeyPlusThreeLamps()
    {
        var (px, azimuths) = SyntheticImages.Downlights(W, H, 12, 0.6, 40);
        var (rig, stats) = Analyse(px);
        Assert.True(stats.OutlierFallback, "every downlight fails the outlier rule, so the fallback fires");
        Assert.Equal(0, stats.RejectedOutlier);
        Assert.Equal(12, stats.Kept);
        var key = Assert.IsType<RigLight>(rig.Key);
        Assert.Equal(3, rig.Lamps.Length);
        foreach (var l in rig.Lamps.Prepend(key))
            Assert.True(azimuths.Min(a => Off(l, a, 40)) < 1, "each light sits on a downlight");
        Assert.Equal(4, rig.Lamps.Prepend(key).Select(l => azimuths.Select((a, i) => (Off(l, a, 40), i)).Min().i).Distinct().Count());
    }

    [Fact] // [AC-ldr-light-estimate-4]
    public void Overcast_NoKeyFillsOnly()
    {
        var px = SyntheticImages.LdrPanorama(W, H, (_, el) =>
        {
            var v = (byte)(el > 0 ? 200 + 40 * Math.Sin(el * Math.PI / 180) : 120);
            return (v, v, v);
        });
        var (rig, stats) = Analyse(px);
        Assert.Null(rig.Key);
        Assert.Empty(rig.Lamps);
        Assert.Equal(0, stats.Components);
        Assert.False(stats.Saturated);
        Assert.All(rig.Regions, r => Assert.True(Y(r.Energy) > 0));
    }

    // ---- Edge cases ----

    [Fact] // [EDGE-ldr-light-estimate-1]
    public void ClippedSunAcrossSeam_OneComponent()
    {
        var px = SyntheticImages.LdrPanorama(W, H, (_, _) => BlueSky);
        SyntheticImages.Lamp(px, W, H, 180, 10, 3, (255, 210, 150));
        var (rig, stats) = Analyse(px);
        Assert.Equal(1, stats.Components);
        Assert.True(Off(Assert.IsType<RigLight>(rig.Key), 180, 10) < 1.5, "key across the seam");
    }

    [Fact] // [EDGE-ldr-light-estimate-2]
    public void ClippedSunNearZenith_KeyCorrect()
    {
        var px = SyntheticImages.LdrPanorama(W, H, (_, _) => BlueSky);
        SyntheticImages.Lamp(px, W, H, 30, 85, 3, (255, 210, 150));
        var (rig, stats) = Analyse(px);
        Assert.Equal(1, stats.Components);
        Assert.True(Off(Assert.IsType<RigLight>(rig.Key), 30, 85) < 1.5, "key near the zenith");
    }

    [Fact] // [EDGE-ldr-light-estimate-3]
    public void BlownOutSkyBand_BroadRejected()
    {
        // A clipped patch of radius 15° (1.7% of the sphere): broad, so no key; its light stays in the fills.
        var px = SyntheticImages.LdrPanorama(W, H, (_, _) => BlueSky);
        SyntheticImages.ClippedDisc(px, W, H, -60, 35, 15);
        var (rig, stats) = Analyse(px);
        Assert.Equal(1, stats.Broad);
        Assert.Null(rig.Key);
        Assert.False(stats.Saturated);
        var patch = 2 * Math.PI * (1 - Math.Cos(15 * Math.PI / 180));
        Assert.True(Y(rig.TotalEnergy) >= 0.95 * patch, "the patch's energy is in the fills");
        Assert.True(Off(rig.Regions[0], -60, 35) < 25, "the top region is the patch");
    }

    [Fact] // [EDGE-ldr-light-estimate-4]
    public void Lamp0p15Degree_IsSpeck()
    {
        // 4096x2048 (0.088°/pixel): a 0.15° disc centred on a pixel centre rasterises to that one pixel, a quarter of a
        // mask cell: it seeds, but its Ω (≈ 2.4e-6 sr) is a speck.
        const int w = 4096, h = 2048;
        var (az, el) = SyntheticImages.PixelAzEl(1000.5, 900.5, w, h);
        var px = SyntheticImages.LdrPanorama(w, h, (_, _) => DimRoom);
        SyntheticImages.Lamp(px, w, h, az, el, 0.15, (255, 190, 120));
        var (rig, stats) = Analyse(px, w, h);
        Assert.Equal(1, stats.Components);
        Assert.Equal(1, stats.Specks);
        Assert.Null(rig.Key);
    }

    [Fact] // [EDGE-ldr-light-estimate-5]
    public void MoreSurvivorsThanSlots_TopFour()
    {
        var px = SyntheticImages.LdrPanorama(W, H, (_, _) => DimRoom);
        double[] sizes = [1.6, 1.4, 1.2, 1.0, 0.9, 0.8];
        for (var i = 0; i < sizes.Length; i++) SyntheticImages.Lamp(px, W, H, -150 + 60 * i, 15, sizes[i], (255, 190, 130));
        var (rig, stats) = Analyse(px);
        Assert.Equal(6, stats.Kept);
        Assert.True(Off(rig.Key!, -150, 15) < 1);
        Assert.Equal(3, rig.Lamps.Length);
        for (var i = 0; i < 3; i++) Assert.True(Off(rig.Lamps[i], -150 + 60 * (i + 1), 15) < 1, $"lamp {i}");
    }

    [Fact] // [EDGE-ldr-light-estimate-5]
    public void EqualBlobs_FirstRowMajorIsKey()
    {
        // Same row, 90° (512 pixels) apart: identical rasterisation, identical Ω. The lower first member cell (the
        // smaller column on the same rows) wins.
        var px = SyntheticImages.LdrPanorama(W, H, (_, _) => DimRoom);
        var (az1, el) = SyntheticImages.PixelAzEl(700.5, 400.5, W, H);
        var (az2, _) = SyntheticImages.PixelAzEl(1212.5, 400.5, W, H);
        SyntheticImages.Lamp(px, W, H, az2, el, 1.0, (255, 190, 130));
        SyntheticImages.Lamp(px, W, H, az1, el, 1.0, (255, 190, 130));
        var r = Detailed(px);
        Assert.Equal(2, r.Stats.Kept);
        Assert.Equal(r.Blobs[0].SolidAngle, r.Blobs[1].SolidAngle);
        Assert.True(Off(r.Rig.Key!, az1, el) < 0.5, "the first in row-major order is the key");
        Assert.True(Off(Assert.Single(r.Rig.Lamps), az2, el) < 0.5);
    }

    [Fact] // [EDGE-ldr-light-estimate-6]
    public void NoRing_WhiteColour_GlowPasses()
    {
        // Built by hand: the probe covers only rows 0-9 (bright, so the median is above 0), the mask has a lamp near
        // probe row 75. Every ring texel is outside the covered rows: an empty ring can't judge the glow, so it passes,
        // and the colour is white.
        var rgb = new float[RadianceProbe.Width * RadianceProbe.Height * 3];
        for (var i = 0; i < 10 * RadianceProbe.Width * 3; i++) rgb[i] = 0.5f;
        var fraction = new float[ClipMask.Width * ClipMask.Height];
        for (var y = 600; y < 606; y++)
        for (var x = 1000; x < 1006; x++) fraction[y * ClipMask.Width + x] = 1f;
        var probe = new RadianceProbe
        {
            Rgb = rgb, CoveredTop = 0, CoveredRows = 10,
            Clip = new ClipMask { Fraction = fraction, CoveredTop = 0, CoveredRows = ClipMask.Height, StrictWhiteShare = 0 },
        };
        var (rig, stats) = LdrLightEstimator.Analyse(probe);
        AssertEnergy(rig);
        Assert.Equal(0, stats.RejectedHalo);
        var key = Assert.IsType<RigLight>(rig.Key);
        Assert.True(Vector3.Distance(Vector3.One, key.Energy) < 1e-5f, $"white, got {key.Energy}");
    }

    [Fact] // [EDGE-ldr-light-estimate-6]
    public void DarkRing_WhiteColour()
    {
        var px = SyntheticImages.LdrPanorama(W, H, (_, el) => el < -30 ? ((byte)60, (byte)50, (byte)40) : ((byte)0, (byte)0, (byte)0));
        SyntheticImages.ClippedDisc(px, W, H, 70, 20, 0.6);
        var (rig, _) = Analyse(px);
        var key = Assert.IsType<RigLight>(rig.Key);
        Assert.True(Vector3.Distance(Vector3.One, key.Energy) < 1e-5f, $"white, got {key.Energy}");
    }

    [Fact] // [EDGE-ldr-light-estimate-7]
    public void AllBlack_EmptyRig()
    {
        var (rig, stats) = Analyse(new byte[W * H * 3]);
        Assert.Null(rig.Key);
        Assert.Empty(rig.Lamps);
        Assert.Equal(Vector3.Zero, rig.TotalEnergy);
        Assert.Equal(0, stats.Components);
        Assert.All(rig.Regions, r => Assert.Equal(Vector3.Zero, r.Energy));
    }

    [Fact] // [EDGE-ldr-light-estimate-7]
    public void AllWhite_SaturatedNoPointSources()
    {
        var px = Enumerable.Repeat((byte)255, W * H * 3).ToArray();
        var (rig, stats) = Analyse(px);
        Assert.True(stats.Saturated);
        Assert.Equal(1.0, stats.ClipShare, 1e-3);
        Assert.Null(rig.Key);
        Assert.Empty(rig.Lamps);
        var quarter = Y(rig.TotalEnergy) / 4;
        Assert.All(rig.Regions, r => Assert.Equal(quarter, Y(r.Energy), quarter * 0.02f));
    }

    [Fact] // [EDGE-ldr-light-estimate-8]
    public void BandPanorama_KeyInBand()
    {
        // 4096x1024 (4:1): a band of ±45°, 64 probe rows from row 32.
        const int w = 4096, h = 1024;
        var px = SyntheticImages.LdrPanorama(w, h, (_, _) => BlueSky);
        SyntheticImages.Lamp(px, w, h, -30, 10, 2, (255, 200, 140));
        var probe = RadianceProbe.BuildSrgb8(px, w, h);
        Assert.Equal((32, 64), (probe.CoveredTop, probe.CoveredRows));
        var (rig, _) = LdrLightEstimator.Analyse(probe);
        AssertEnergy(rig);
        var key = Assert.IsType<RigLight>(rig.Key);
        Assert.True(Off(key, -30, 10) < 1.5, "key in the band");
        Assert.InRange(key.T * RadianceProbe.Height, 32, 96);
    }

    [Theory] // [AC-ldr-light-estimate-1] [EDGE-ldr-light-estimate-9]
    [InlineData(255, 160, 40, false)]    // orange signage: min 40 < 160
    [InlineData(244, 244, 244, false)]
    [InlineData(249, 249, 200, false)]   // max < 250 and min < 245
    [InlineData(255, 159, 255, false)]   // min 159
    [InlineData(245, 245, 245, true)]
    [InlineData(255, 255, 255, true)]
    [InlineData(255, 254, 185, true)]    // the sunset sun core
    [InlineData(250, 200, 160, false)]   // G under 250 (revision 6)
    [InlineData(175, 215, 255, false)]   // pale sky
    [InlineData(250, 235, 200, false)]   // sand
    [InlineData(255, 222, 196, false)]   // skin
    [InlineData(255, 250, 210, true)]    // a cream wall in full sun (a documented limit)
    [InlineData(255, 230, 170, false)]   // a warm lamp core (a documented limit)
    [InlineData(250, 250, 160, true)]    // the warm branch's corner
    [InlineData(250, 250, 159, false)]   // B under 160
    [InlineData(249, 255, 200, false)]   // R under 250
    public void IsClipped_Rule(int r, int g, int b, bool clipped)
    {
        Assert.Equal(clipped, LdrLightEstimator.IsClipped((byte)r, (byte)g, (byte)b));
        // The strict-white predicate (gate (c)) is min(R,G,B) ≥ 245, whatever the warm branch says.
        var strict = Math.Min(r, Math.Min(g, b)) >= 245;
        Assert.Equal(strict, LdrLightEstimator.IsStrictWhite((byte)r, (byte)g, (byte)b));
        Assert.True(!strict || clipped);
        // The probe's mask uses the same rule.
        var px = new byte[256 * 128 * 3];
        px[0] = (byte)r; px[1] = (byte)g; px[2] = (byte)b;
        Assert.Equal(clipped ? 1f : 0f, RadianceProbe.BuildSrgb8(px, 256, 128).Clip!.Fraction[0]);
    }

    [Fact] // [EDGE-ldr-light-estimate-27] [EDGE-ldr-light-estimate-47]
    public void WarmClippedSkyBand_DoesNotSaturate_SunIsKey()
    {
        // Everything above 20° is a cream sky that the warm-core branch clips (33% of the sphere). The saturated-image
        // gate counts only near-white clips, so the white sun below it is still the key; the cream band is broad.
        var px = SyntheticImages.LdrPanorama(W, H, (_, el) =>
            el > 20 ? ((byte)255, (byte)250, (byte)210) : el > 0 ? ((byte)90, (byte)140, (byte)220) : ((byte)60, (byte)50, (byte)40));
        SyntheticImages.Lamp(px, W, H, 70, 8, 2, (255, 200, 120));
        var (rig, stats) = Analyse(px);
        Assert.False(stats.Saturated);
        Assert.True(stats.ClipShare > 0.25, $"warm-clipped share {stats.ClipShare}");   // would have saturated the image
        Assert.True(stats.StrictWhiteShare < 0.01, $"strict-white share {stats.StrictWhiteShare}");
        var r = Detailed(px);
        var band = r.Blobs.Where(b => b.Verdict == BlobVerdict.Broad).ToList();
        Assert.True(stats.Broad >= 1 && band.Count == stats.Broad, "the cream band is counted as broad");
        Assert.Contains(band, b => b.Direction.Y > 0.8f && b.SolidAngle > 0.25 * 4 * Math.PI);
        Assert.True(Off(Assert.IsType<RigLight>(rig.Key), 70, 8) < 1.5, "the sun is the key");
        // The same with a pale blue sky band (not clipped at all).
        var pale = SyntheticImages.LdrPanorama(W, H, (_, el) => el > 20 ? ((byte)175, (byte)215, (byte)255) : ((byte)60, (byte)50, (byte)40));
        SyntheticImages.Lamp(pale, W, H, 70, 8, 2, (255, 200, 120));
        Assert.True(Off(Assert.IsType<RigLight>(Analyse(pale).Rig.Key), 70, 8) < 1.5);
    }

    [Fact] // [EDGE-ldr-light-estimate-9]
    public void OrangeSignageCore_NotALight()
    {
        var px = SyntheticImages.LdrPanorama(W, H, (_, _) => BlueSky);
        SyntheticImages.ClippedDisc(px, W, H, 40, 30, 1, (255, 160, 40));
        var (rig, stats) = Analyse(px);
        Assert.Equal(0, stats.Components);
        Assert.Null(rig.Key);
    }

    [Fact] // [AC-ldr-light-estimate-1] [EDGE-ldr-light-estimate-46]
    public void YellowSunsetCore_IsKey()
    {
        // A 1.5° sun core of (255,254,185) on an orange sky band with a warm glow: no pixel has min ≥ 245, so only the
        // warm-core branch clips it.
        var px = SyntheticImages.LdrPanorama(W, H, (_, el) => el > 0 ? ((byte)230, (byte)140, (byte)80) : ((byte)60, (byte)45, (byte)35));
        SyntheticImages.Ring(px, W, H, -30, 6, 0, 0.75 + 4, (255, 190, 110));
        SyntheticImages.ClippedDisc(px, W, H, -30, 6, 0.75, (255, 254, 185));
        for (var i = 0; i < px.Length; i += 3) Assert.True(Math.Min(px[i], Math.Min(px[i + 1], px[i + 2])) < 245);
        var (rig, _) = Analyse(px);
        var key = Assert.IsType<RigLight>(rig.Key);
        Assert.True(Off(key, -30, 6) < 1.5, "key at the sun");
        Assert.True(key.Energy.X > key.Energy.Y && key.Energy.Y > key.Energy.Z, $"warm, got {key.Energy}");
    }

    [Fact] // [EDGE-ldr-light-estimate-22]
    public void LampsHalfDegreeApart_Separate()
    {
        // Two 0.6° lamps whose clipped edges are 0.5° apart (about 2.8 cells) with glow between: two lamps.
        var px = SyntheticImages.LdrPanorama(W, H, (_, _) => DimRoom);
        SyntheticImages.Ring(px, W, H, 0.55, 0, 0, 5, (255, 190, 130));
        SyntheticImages.ClippedDisc(px, W, H, 0, 0, 0.3);
        SyntheticImages.ClippedDisc(px, W, H, 1.1, 0, 0.3);
        var (rig, stats) = Analyse(px);
        Assert.Equal(2, stats.Components);
        Assert.NotNull(rig.Key);
        Assert.Single(rig.Lamps);
    }

    [Fact] // [EDGE-ldr-light-estimate-22]
    public void LampsTouching_MergeIntoOne()
    {
        var px = SyntheticImages.LdrPanorama(W, H, (_, _) => DimRoom);
        SyntheticImages.Ring(px, W, H, 0.3, 0, 0, 5, (255, 190, 130));
        SyntheticImages.ClippedDisc(px, W, H, 0, 0, 0.3);
        SyntheticImages.ClippedDisc(px, W, H, 0.6, 0, 0.3);
        var (rig, stats) = Analyse(px);
        Assert.Equal(1, stats.Components);
        Assert.True(Off(rig.Key!, 0.3, 0) < 0.5, "one lamp between the two");
        Assert.Empty(rig.Lamps);
    }

    [Fact] // [EDGE-ldr-light-estimate-23]
    public void SunTouchingSmallCloudEdge_KeyWithin2Degrees()
    {
        // A clipped cloud edge (a 1° disc) touching the 2° sun: one small component, still the key, within 2°.
        var px = SyntheticImages.LdrPanorama(W, H, (_, _) => BlueSky);
        SyntheticImages.Lamp(px, W, H, 40, 30, 2, (255, 215, 150), glowDeg: 6);
        SyntheticImages.ClippedDisc(px, W, H, 41.7, 30, 0.6);
        var (rig, stats) = Analyse(px);
        Assert.Equal(1, stats.Components);
        Assert.True(Off(Assert.IsType<RigLight>(rig.Key), 40, 30) < 2);
    }

    [Fact] // [EDGE-ldr-light-estimate-23]
    public void SunMergedIntoLargeCloud_NoKey_TopRegionContainsSun()
    {
        // The sun touches a clipped cloud of radius 14°: the merged component is broad, so there is no key (the stated
        // fallback), and the top median-cut region is the bright cloud and sun.
        var px = SyntheticImages.LdrPanorama(W, H, (_, _) => BlueSky);
        SyntheticImages.ClippedDisc(px, W, H, 40, 30, 1);
        SyntheticImages.ClippedDisc(px, W, H, 40, 15, 14.2);
        var (rig, stats) = Analyse(px);
        Assert.Equal(1, stats.Components);
        Assert.Equal(1, stats.Broad);
        Assert.Null(rig.Key);
        // Equal-energy regions ranked by radiance: the one holding the bright cloud and sun is the smallest, so first.
        var nearest = rig.Regions.OrderBy(r => Off(r, 40, 30)).First();
        Assert.Same(nearest, rig.Regions[0]);
    }

    [Fact] // [EDGE-ldr-light-estimate-24]
    public void ScatteredSinglePixels_NothingKept_NoGiantComponent()
    {
        const int w = 4096, h = 2048;
        var px = SyntheticImages.WithScatter(SyntheticImages.LdrPanorama(w, h, (_, _) => (128, 128, 128)), w, h, 11, 0.02);
        var r = Detailed(px, w, h);
        Assert.True(r.Stats.Components > 1000);
        Assert.Equal(0, r.Stats.Broad);
        Assert.Equal(0, r.Stats.Kept);
        Assert.Null(r.Rig.Key);
        Assert.All(r.Blobs, b => Assert.True(b.RadiusDegrees < 1, $"component radius {b.RadiusDegrees}°"));
    }

    [Fact] // [EDGE-ldr-light-estimate-24]
    public void ScatterPlusLamp_LampKept_RadiusBounded()
    {
        const int w = 4096, h = 2048;
        var px = SyntheticImages.LdrPanorama(w, h, (_, _) => (128, 128, 128));
        SyntheticImages.Lamp(px, w, h, 50, 20, 1.0, (255, 215, 150));
        SyntheticImages.WithScatter(px, w, h, 11, 0.02);
        var r = Detailed(px, w, h);
        var key = Assert.IsType<RigLight>(r.Rig.Key);
        Assert.True(Off(key, 50, 20) < 1);
        var blob = r.Blobs.Single(b => b.Verdict == BlobVerdict.Key);
        Assert.True(blob.RadiusDegrees <= 0.5 + 0.5, $"radius {blob.RadiusDegrees}° ≤ true radius + 0.5°");
    }

    [Fact] // [EDGE-ldr-light-estimate-25]
    public void DenseStarFieldWithMoon_MoonIsKey_NoStarLamps()
    {
        const int w = 1024, h = 512;
        var (px, _) = SyntheticImages.StarField(w, h, 25, 300, 2, 2, 80, (-50, 20));
        SyntheticImages.ClippedDisc(px, w, h, -50, 20, 0.75);
        var (rig, stats) = Analyse(px, w, h);
        Assert.True(Off(Assert.IsType<RigLight>(rig.Key), -50, 20) < 1, "the moon is the key");
        Assert.Empty(rig.Lamps);
        Assert.True(stats.RejectedOutlier >= 299, $"rejected outliers {stats.RejectedOutlier}");
        Assert.False(stats.OutlierFallback);
    }

    [Fact] // [EDGE-ldr-light-estimate-26]
    public void WhitePatchDarkHalo_NotALight()
    {
        var px = SyntheticImages.LdrPanorama(W, H, (_, _) => (200, 200, 200));
        SyntheticImages.Ring(px, W, H, 10, 40, 0, 5, (60, 60, 60));
        SyntheticImages.ClippedDisc(px, W, H, 10, 40, 0.5);
        var (rig, stats) = Analyse(px);
        Assert.Equal(1, stats.RejectedHalo);
        Assert.Null(rig.Key);
    }

    [Fact] // [EDGE-ldr-light-estimate-27]
    public void OverQuarterClipped_Saturated_NoPointSources()
    {
        // Everything above 20° is clipped (33% of the sphere), plus a separate glowing lamp below the horizon.
        var px = SyntheticImages.LdrPanorama(W, H, (_, el) => el > 20 ? ((byte)255, (byte)255, (byte)255) : ((byte)80, (byte)70, (byte)60));
        SyntheticImages.Lamp(px, W, H, 0, -20, 1, (255, 190, 130));
        var (rig, stats) = Analyse(px);
        Assert.True(stats.Saturated);
        Assert.True(stats.ClipShare > 0.25);
        Assert.Null(rig.Key);
        Assert.Empty(rig.Lamps);
    }

    [Fact] // [EDGE-ldr-light-estimate-31]
    public void SaturatedHalo_CappedTowardWhite()
    {
        var px = SyntheticImages.LdrPanorama(W, H, (_, _) => DimRoom);
        SyntheticImages.Lamp(px, W, H, -80, 10, 1.2, (255, 20, 20));
        var (rig, _) = Analyse(px);
        var key = Assert.IsType<RigLight>(rig.Key);
        Assert.Equal(0.5f, Saturation(key.Energy), 0.01f);
        Assert.Equal(1f, Y(key.Energy), 1e-3f);
        Assert.True(key.Energy.X > key.Energy.Y);
    }

    [Fact] // [EDGE-ldr-light-estimate-40]
    public void BlackBackgroundOneLamp_KeyFound()
    {
        var px = new byte[W * H * 3];
        SyntheticImages.ClippedDisc(px, W, H, 100, 25, 0.5);
        var (rig, _) = Analyse(px);
        Assert.NotNull(rig.Key);
        Assert.Equal(Vector3.Zero, rig.TotalEnergy);   // the lamp's texels are zeroed: nothing else is lit
        var resolved = LightRigPlanner.Resolve(rig);
        Assert.Equal(LightRigLayout.EstimatedKeyOverSky * LightRigLayout.EstimatedSkyFloorY, Y(resolved.Key!.Energy), 1e-4f);
    }

    [Fact] // [EDGE-ldr-light-estimate-45]
    public void PureStarField_FallbackBehaviour()
    {
        // ND-5 (adopted): 300 point blobs survive (a)-(d), more than FallbackMaxBlobs (32), so the fallback doesn't fire:
        // no key, no lamps, fills only.
        const int w = 1024, h = 512;
        var (px, _) = SyntheticImages.StarField(w, h, 45, 300, 2, 2, 80);
        var (rig, stats) = Analyse(px, w, h);
        Assert.False(stats.OutlierFallback);
        Assert.Equal(300, stats.RejectedOutlier + stats.RejectedShare);
        Assert.True(stats.RejectedOutlier > LdrLightEstimator.FallbackMaxBlobs);
        Assert.Null(rig.Key);
        Assert.Empty(rig.Lamps);
        Assert.Equal(32, LdrLightEstimator.FallbackMaxBlobs);
    }
    [Theory] // [EDGE-ldr-light-estimate-42] [EDGE-ldr-light-estimate-45]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public void FallbackMaxBlobs_Boundary(int count, bool fires)
    {
        // Equal glowing 0.6° lamps around one row (el 30°). 32 pass (a)-(d), all fail (e): the fallback fires (32 ≤
        // FallbackMaxBlobs). 33: it doesn't, so no key.
        var px = SyntheticImages.LdrPanorama(W, H, (_, _) => (39, 39, 39));
        var azimuths = Enumerable.Range(0, count).Select(i => -180 + 360.0 * i / count + 5).ToArray();
        foreach (var az in azimuths) SyntheticImages.Lamp(px, W, H, az, 30, 0.6, (110, 85, 60), glowDeg: 3.5);
        var (rig, stats) = Analyse(px);
        Assert.Equal(0, stats.Specks + stats.Broad + stats.RejectedShare);
        Assert.Equal(fires, stats.OutlierFallback);
        if (fires)
        {
            Assert.Equal(count, stats.Kept);
            Assert.True(azimuths.Min(a => Off(Assert.IsType<RigLight>(rig.Key), a, 30)) < 1);
            Assert.Equal(3, rig.Lamps.Length);
        }
        else
        {
            Assert.Equal(count, stats.RejectedOutlier);
            Assert.Null(rig.Key);
            Assert.Empty(rig.Lamps);
        }
    }

    [Fact] // [EDGE-ldr-light-estimate-8]
    public void BandMaskRows_MapInsideCoveredProbeRows()
    {
        // 1000x200 (5:1): the probe band is 51 rows from 38, the mask band 410 rows from 307. Under a plain row / 8 the
        // mask's last rows (712-716) would land on probe row 89, outside the band.
        const int w = 1000, h = 200;
        var probe = RadianceProbe.BuildSrgb8(new byte[w * h * 3], w, h);
        var mask = probe.Clip!;
        Assert.Equal((38, 51), (probe.CoveredTop, probe.CoveredRows));
        Assert.Equal((307, 410), (mask.CoveredTop, mask.CoveredRows));
        for (var cy = mask.CoveredTop; cy < mask.CoveredTop + mask.CoveredRows; cy++)
        foreach (var cx in new[] { 0, 1000, ClipMask.Width - 1 })
        {
            var t = LdrLightEstimator.TexelOf(cy * ClipMask.Width + cx, mask, probe);
            Assert.InRange(t / RadianceProbe.Width, probe.CoveredTop, probe.CoveredTop + probe.CoveredRows - 1);
            Assert.Equal(cx * RadianceProbe.Width / ClipMask.Width, t % RadianceProbe.Width);
        }
        // A full frame maps exactly row / 8.
        var full = RadianceProbe.BuildSrgb8(new byte[512 * 256 * 3], 512, 256);
        for (var cy = 0; cy < ClipMask.Height; cy += 7)
            Assert.Equal(cy / 8 * RadianceProbe.Width + 3, LdrLightEstimator.TexelOf(cy * ClipMask.Width + 24, full.Clip!, full));

        // End to end: a lamp on the band's bottom edge is found and its texels are zeroed inside the band.
        var px = SyntheticImages.LdrPanorama(w, h, (_, _) => (60, 50, 40));
        var (az, el) = SyntheticImages.PixelAzEl(500.5, 197.5, w, h);
        SyntheticImages.Lamp(px, w, h, az, el, 1.2, (255, 190, 120), glowDeg: 3);
        var (rig, _) = LdrLightEstimator.Analyse(RadianceProbe.BuildSrgb8(px, w, h));
        AssertEnergy(rig);
        Assert.NotNull(rig.Key);
    }
}
