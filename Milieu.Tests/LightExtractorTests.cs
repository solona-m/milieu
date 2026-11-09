using System;
using System.Linq;
using System.Numerics;
using Milieu.Lighting;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public class LightExtractorTests
{
    private static LightRig Extract(float[] rgb, int w, int h) => LightExtractor.Extract(RadianceProbe.Build(rgb, w, h));

    private static Vector3 DirOf(RigLight l)
    {
        var (az, el) = SyntheticImages.AzEl(l.S, l.T);
        return SyntheticImages.Dir(az, el);
    }

    private static float Y(Vector3 e) => 0.2126f * e.X + 0.7152f * e.Y + 0.0722f * e.Z;

    private static readonly (float, float, float) DimSky = (0.02f, 0.03f, 0.05f);

    [Fact] // [AC-scene-lighting-1]
    public void SunDisc_KeyDirectionColourEnergy()
    {
        const double az = 40, el = 30, radius = 3;
        var img = SyntheticImages.SunDisc(1024, 512, az, el, radius, (8f, 6f, 4f), (_, _) => DimSky);
        var rig = Extract(img, 1024, 512);

        var key = Assert.IsType<RigLight>(rig.Key);
        Assert.True(SyntheticImages.AngleDeg(DirOf(key), SyntheticImages.Dir(az, el)) < 1.5, "key direction");
        Assert.Equal(8f / 6f, key.Energy.X / key.Energy.Y, 8f / 6f * 0.02f);
        Assert.Equal(4f / 6f, key.Energy.Z / key.Energy.Y, 4f / 6f * 0.02f);
        var omega = 2 * Math.PI * (1 - Math.Cos(radius * Math.PI / 180));
        Assert.Equal(6 * omega, key.Energy.Y, 6 * omega * 0.05);
    }

    [Fact] // [AC-scene-lighting-1]
    public void SkyAndGround_RegionsRankedAndColoured()
    {
        var img = SyntheticImages.HdrPanorama(512, 256, (_, el) => el > 0 ? (0.3f, 0.5f, 1.0f) : (0.2f, 0.15f, 0.1f));
        var rig = Extract(img, 512, 256);
        Assert.Equal(LightRig.RegionCount, rig.Regions.Length);
        var sum = rig.Regions.Aggregate(rig.Key?.Energy ?? Vector3.Zero, (a, r) => a + r.Energy);
        Assert.True(Vector3.Distance(sum, rig.TotalEnergy) <= 1e-3f * rig.TotalEnergy.Length(), "regions + key = total");
        var top = rig.Regions[0];
        Assert.True(top.T < 0.5f, "top region in the upper hemisphere");
        Assert.True(top.Energy.Z > top.Energy.X && top.Energy.Z > top.Energy.Y, "top region blue-dominant");
        // Ranked by energy per solid angle: the two pure-sky caps (radiance 0.53) come before the horizon-and-ground
        // halves, which hold more energy over a larger solid angle but less per steradian.
        Assert.True(rig.Regions[0].T < 0.5f && rig.Regions[1].T < 0.5f);
        Assert.True(rig.Regions[2].T > rig.Regions[0].T && rig.Regions[3].T > rig.Regions[1].T);
    }

    [Fact] // [AC-scene-lighting-5]
    public void Overcast_NoKey()
    {
        var img = SyntheticImages.HdrPanorama(512, 256, (_, el) =>
        {
            var v = el > 0 ? 1f + 0.5f * (float)Math.Sin(el * Math.PI / 180) : 0.3f;
            return (v, v, v);
        });
        var rig = Extract(img, 512, 256);
        Assert.Null(rig.Key);
        Assert.True(Y(rig.TotalEnergy) > 0);
    }

    [Fact] // [EDGE-scene-lighting-1]
    public void SunAcrossSeam_KeyCorrect()
    {
        var img = SyntheticImages.SunDisc(1024, 512, 180, 10, 3, (50f, 45f, 40f), (_, _) => DimSky);
        var key = Assert.IsType<RigLight>(Extract(img, 1024, 512).Key);
        Assert.True(SyntheticImages.AngleDeg(DirOf(key), SyntheticImages.Dir(180, 10)) < 1.5, "key across the seam");
    }

    [Fact] // [EDGE-scene-lighting-2]
    public void SunNearZenith_KeyCorrect()
    {
        var img = SyntheticImages.SunDisc(1024, 512, 30, 85, 3, (50f, 45f, 40f), (_, _) => DimSky);
        var key = Assert.IsType<RigLight>(Extract(img, 1024, 512).Key);
        Assert.True(SyntheticImages.AngleDeg(DirOf(key), SyntheticImages.Dir(30, 85)) < 1.5, "key near the zenith");
        var low = SyntheticImages.SunDisc(1024, 512, -70, -84, 3, (50f, 45f, 40f), (_, _) => DimSky);
        var lowKey = Assert.IsType<RigLight>(Extract(low, 1024, 512).Key);
        Assert.True(SyntheticImages.AngleDeg(DirOf(lowKey), SyntheticImages.Dir(-70, -84)) < 1.5, "key near the nadir");
    }

    [Fact] // [EDGE-scene-lighting-3]
    public void Uniform_FourRegionsEqualEnergy()
    {
        var img = SyntheticImages.HdrPanorama(512, 256, (_, _) => (1f, 1f, 1f));
        var rig = Extract(img, 512, 256);
        Assert.Null(rig.Key);
        var quarter = Y(rig.TotalEnergy) / 4;
        Assert.All(rig.Regions, r => Assert.Equal(quarter, Y(r.Energy), quarter * 0.02f));
        Assert.Equal(4 * Math.PI, Y(rig.TotalEnergy), 4 * Math.PI * 0.01);
    }

    [Fact] // [EDGE-scene-lighting-4]
    public void AllBlack_EmptyRig()
    {
        var rig = Extract(new float[512 * 256 * 3], 512, 256);
        Assert.Null(rig.Key);
        Assert.Equal(Vector3.Zero, rig.TotalEnergy);
        Assert.Equal(LightRig.RegionCount, rig.Regions.Length);
        Assert.All(rig.Regions, r =>
        {
            Assert.Equal(Vector3.Zero, r.Energy);
            Assert.InRange(r.S, 0f, 1f);
            Assert.InRange(r.T, 0f, 1f);
        });
    }

    [Fact] // [EDGE-scene-lighting-8]
    public void TiedPeaks_FirstRowMajorWins()
    {
        // 256x128 maps 1:1 onto the probe. Two identical 3x3 blocks; the upper one comes first in row-major order.
        const int w = RadianceProbe.Width, h = RadianceProbe.Height;
        var img = new float[w * h * 3];
        for (var i = 0; i < img.Length; i++) img[i] = 0.01f;
        void Block(int x0, int y0)
        {
            for (var y = y0; y < y0 + 3; y++)
            for (var x = x0; x < x0 + 3; x++)
            {
                var o = (y * w + x) * 3;
                img[o] = img[o + 1] = img[o + 2] = 1000f;
            }
        }
        Block(200, 60);
        Block(40, 30);
        var key = Assert.IsType<RigLight>(Extract(img, w, h).Key);
        var (az1, el1) = SyntheticImages.AzEl(41.5 / w, 31.5 / h);
        var (az2, el2) = SyntheticImages.AzEl(201.5 / w, 61.5 / h);
        Assert.True(SyntheticImages.AngleDeg(DirOf(key), SyntheticImages.Dir(az1, el1)) < 2, "the first block is the key");
        Assert.True(SyntheticImages.AngleDeg(DirOf(key), SyntheticImages.Dir(az2, el2)) > 30);
    }

    [Fact] // [EDGE-scene-lighting-37]
    public void BandOvercast_NoKey_MedianOverCoveredOnly()
    {
        // 16:1 → a 16-row band (±11.25°) with a soft glow 6x brighter within 6° of (0°, 0°). Over all texels the median is
        // 0 (the empty rows) and the glow carries > 5% of the energy, so only the covered-texel median keeps it from
        // being taken for a sun.
        var img = SyntheticImages.HdrPanorama(1024, 64, (az, el) =>
        {
            // Band images span 180·2h/w degrees of elevation: rescale the helper's full-frame elevation.
            var bandEl = el * (180.0 * 2 * 64 / 1024) / 180.0;
            var near = SyntheticImages.AngleDeg(SyntheticImages.Dir(az, bandEl), SyntheticImages.Dir(0, 0)) <= 6;
            var v = near ? 6f : 1f;
            return (v, v, v);
        });
        var probe = RadianceProbe.Build(img, 1024, 64);
        Assert.Equal(16, probe.CoveredRows);
        var rig = LightExtractor.Extract(probe);
        Assert.Null(rig.Key);

        var y = probe.Rgb.Where((_, i) => i % 3 == 0).Select(v => (double)v).ToArray();
        Assert.Equal(1.0, LightExtractor.CoveredMedian(y, probe), 3);
        Assert.Equal(0.0, y.Order().ElementAt(y.Length / 2));   // the all-texel median the rule must not use
    }

    [Fact] // [AC-scene-lighting-1]
    public void Deterministic()
    {
        var img = SyntheticImages.SunDisc(512, 256, -100, 20, 4, (20f, 18f, 15f), (_, el) => el > 0 ? (0.2f, 0.3f, 0.6f) : (0.1f, 0.08f, 0.05f));
        var a = Extract(img, 512, 256);
        var b = Extract(img, 512, 256);
        Assert.Equal(a.Key, b.Key);
        Assert.Equal(a.Regions, b.Regions);
        Assert.Equal(a.TotalEnergy, b.TotalEnergy);
        Assert.Equal(LightExtractor.Version, a.Version);
    }

    /// <summary>A 256x128 grey source maps 1:1 onto the probe, so texel values are exact.</summary>
    private static LightRig Grey1to1(Func<int, int, float> v)
    {
        const int w = RadianceProbe.Width, h = RadianceProbe.Height;
        var img = new float[w * h * 3];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var o = (y * w + x) * 3;
            img[o] = img[o + 1] = img[o + 2] = v(x, y);
        }
        return Extract(img, w, h);
    }

    // Row 21 is elevation 59.8°: one texel there holds dω ≈ 3.0e-4 sr. On a background of 2 (total ≈ 25.1), a texel of
    // 1800 holds ≈ 2.1% of the energy at 900x the median; one of 417 holds ≈ 0.5% at about 209x.
    private const int SunRow = 21, SunCol = 100;

    [Fact] // [AC-ldr-light-estimate-9] [EDGE-ldr-light-estimate-36]
    public void SmallStrongSun_LowShare_IsKey()
    {
        var rig = Grey1to1((x, y) => x == SunCol && y == SunRow ? 1800f : 2f);
        var key = Assert.IsType<RigLight>(rig.Key);
        var share = Y(key.Energy) / Y(rig.TotalEnergy);
        Assert.InRange(share, 0.01f, 0.05f);   // under the 5% share: only the strong-peak branch makes it a key
        var (az, el) = SyntheticImages.AzEl((SunCol + 0.5) / RadianceProbe.Width, (SunRow + 0.5) / RadianceProbe.Height);
        Assert.True(SyntheticImages.AngleDeg(DirOf(key), SyntheticImages.Dir(az, el)) < 0.5);
    }

    [Fact] // [AC-ldr-light-estimate-9] [EDGE-ldr-light-estimate-37]
    public void Overcast_PeakSeven_NoKey()
    {
        // A bright patch 7x the median within 6° of (0°, 20°): a large share, but no peak.
        var centre = SyntheticImages.Dir(0, 20);
        var rig = Grey1to1((x, y) =>
        {
            var (az, el) = SyntheticImages.AzEl((x + 0.5) / RadianceProbe.Width, (y + 0.5) / RadianceProbe.Height);
            return SyntheticImages.AngleDeg(SyntheticImages.Dir(az, el), centre) <= 6 ? 7f : 1f;
        });
        Assert.Null(rig.Key);
    }

    [Fact] // [EDGE-ldr-light-estimate-38]
    public void ModeratePeak_LowShare_NoKey()
    {
        // A 3x3 block of 100 on 2 at the equator: 50x the median, ≈ 2% of the energy.
        var rig = Grey1to1((x, y) => x >= 100 && x < 103 && y >= 63 && y < 66 ? 100f : 2f);
        Assert.Null(rig.Key);
    }

    [Fact] // [AC-ldr-light-estimate-9] [EDGE-ldr-light-estimate-43]
    public void NightSky_ZeroMedian_StarNotKey()
    {
        // Black above a dim ground glow (the bottom 40% of rows): the covered median is 0. One star of 500 at 60° holds
        // about 3% of the energy: under 5%, and the strong branch needs a non-zero median.
        var rig = Grey1to1((x, y) => x == SunCol && y == SunRow ? 500f : y >= 77 ? 1f : 0f);
        Assert.Null(rig.Key);
    }

    [Fact] // [EDGE-ldr-light-estimate-44]
    public void StrongPeak_BelowOnePercent_NoKey()
    {
        var rig = Grey1to1((x, y) => x == SunCol && y == SunRow ? 417f : 2f);
        Assert.Null(rig.Key);
        // The same image with a 1800 sun (2%) is a key: only the share differs.
        Assert.NotNull(Grey1to1((x, y) => x == SunCol && y == SunRow ? 1800f : 2f).Key);
    }
}
