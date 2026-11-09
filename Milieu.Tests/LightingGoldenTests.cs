using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Milieu.Lighting;
using Xunit;

namespace Milieu.Tests;

/// <summary>
/// Goldens that pin the lighting arithmetic (docs/adr/0009, 0010). The probe golden is a SHA-256 of the probe's bytes
/// (plain arithmetic, captured on the code before the LDR estimate work); refactors must keep it. The extractor and
/// estimator goldens compare the serialised rig's values with committed literals at 1e-6 relative (absolute floor 1e-9),
/// as cross-CPU byte identity is not promised (ADR 0005).
/// </summary>
public class LightingGoldenTests
{
    /// <summary>A deterministic float in [0, 10) from (x, y, c, seed): an integer hash, no library RNG.</summary>
    internal static float Noise(int x, int y, int c, uint seed)
    {
        var h = (uint)x * 0x9E3779B1u ^ (uint)y * 0x85EBCA77u ^ (uint)c * 0xC2B2AE3Du ^ seed * 0x27D4EB2Fu;
        h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
        return (h & 0xFFFFFF) / (float)0x1000000 * 10f;
    }

    internal static float[] GoldenSource(string name, out int w, out int h)
    {
        (w, h, var seed) = name switch
        {
            "special" => (300, 150, 1u),
            "band" => (1000, 200, 2u),
            "nonInteger" => (700, 350, 3u),
            "upsample" => (100, 50, 4u),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
        var rgb = new float[w * h * 3];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        for (var c = 0; c < 3; c++)
        {
            var v = Noise(x, y, c, seed);
            // A few bright texels so the large-value path matters too.
            if (Noise(x, y, c, seed + 100) > 9.95f) v *= 1000f;
            rgb[(y * w + x) * 3 + c] = v;
        }
        if (name == "special")
        {
            for (var i = 0; i < rgb.Length; i += 97)
                rgb[i] = (i / 97 % 5) switch
                {
                    0 => float.NaN,
                    1 => float.PositiveInfinity,
                    2 => float.NegativeInfinity,
                    3 => -3.5f,
                    _ => 1e9f,
                };
        }
        return rgb;
    }

    public static string Sha(float[] data) => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(data.AsSpan()))).ToLowerInvariant();

    [Theory] // [AC-ldr-light-estimate-7]
    [InlineData("special", 0, 128, "8fd092142cc2a7616f41e5fc1d01917516693e073962bbe0fb3550f0d7089b40")]
    [InlineData("band", 38, 51, "7584c836ffd0c128c2cb7e22a27f51684be612c10694edf086b498b987278bd5")]
    [InlineData("nonInteger", 0, 128, "fa1ac9a908176357b0fb0d530fe1852a0dc52c74a7257877034b6767911adce9")]
    [InlineData("upsample", 0, 128, "2fa1672a734d509b63fbba50cd38ec29d8935d2b0951bed4f304a329e1bbf03c")]
    public void RadianceProbeBuild_Sha256(string name, int coveredTop, int coveredRows, string sha)
    {
        var src = GoldenSource(name, out var w, out var h);
        var p = RadianceProbe.Build(src, w, h);
        Assert.Equal(coveredTop, p.CoveredTop);
        Assert.Equal(coveredRows, p.CoveredRows);
        Assert.True(sha == Sha(p.Rgb), $"RadianceProbe.Build output changed for '{name}': {Sha(p.Rgb)}. The probe must stay bit-identical.");
    }
    /// <summary>The fixed HDR probe of the extractor golden: hash noise in [0, 1) with a 3x3 sun of 5000 at (60, 30).</summary>
    internal static RadianceProbe ExtractGoldenProbe()
    {
        const int w = RadianceProbe.Width, h = RadianceProbe.Height;
        var rgb = new float[w * h * 3];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        for (var c = 0; c < 3; c++)
            rgb[(y * w + x) * 3 + c] = Noise(x, y, c, 7u) * 0.1f * (c == 2 ? 1.5f : 1f);
        for (var y = 30; y < 33; y++)
        for (var x = 60; x < 63; x++)
        {
            var o = (y * w + x) * 3;
            rgb[o] = 5000f; rgb[o + 1] = 4500f; rgb[o + 2] = 4000f;
        }
        return RadianceProbe.Build(rgb, w, h);
    }

    private const string ExtractGoldenJson =
        """
        {"version":2,"key":{"s":0.24023438,"t":0.24619286,"r":18.927557,"g":17.034801,"b":15.142046},"regions":[{"s":0.25017533,"t":0.2502487,"r":1.5725,"g":1.5833718,"b":2.3548086},{"s":0.24956119,"t":0.7488588,"r":1.5704882,"g":1.5834409,"b":2.3564317},{"s":0.7501868,"t":0.2489814,"r":1.583448,"g":1.5638938,"b":2.3360412},{"s":0.75008595,"t":0.75060546,"r":1.5624532,"g":1.563402,"b":2.3437428}],"total":{"r":25.216446,"g":23.32891,"b":24.53307}}
        """;

    [Fact] // [AC-ldr-light-estimate-7] [EDGE-ldr-light-estimate-35]
    public void ExtractGolden_ValuesWithinTolerance()
    {
        var actual = LightRigStore.Serialise(LightExtractor.Extract(ExtractGoldenProbe()));
        AssertJsonClose(ExtractGoldenJson, actual,
            "LightExtractor's output changed. If the change is intended, bump LightExtractor.Version, regenerate the bundled "
            + "light rigs (PrepareBackdrops rigs) and capture this golden again.");
    }

    /// <summary>
    /// The fixed 8-bit source of the estimator golden (integer arithmetic only, 1024x512): a noisy sky over dark ground,
    /// a white lamp with a warm glow, a yellow core (the warm-core clip branch) with its own glow, and a small speck.
    /// </summary>
    internal static byte[] EstimateGoldenSource(out int w, out int h)
    {
        w = 1024; h = 512;
        var px = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var o = (y * w + x) * 3;
            var n = (int)Noise(x, y, 0, 11u);   // 0..9
            if (y < h / 2) { px[o] = (byte)(80 + n); px[o + 1] = (byte)(120 + n); px[o + 2] = (byte)(200 + n); }
            else { px[o] = (byte)(50 + n); px[o + 1] = (byte)(40 + n); px[o + 2] = (byte)(30 + n); }
        }
        void Disc(int cx, int cy, int r2, byte r, byte g, byte b)
        {
            for (var y = cy - 40; y <= cy + 40; y++)
            for (var x = cx - 40; x <= cx + 40; x++)
            {
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > r2) continue;
                var o = (y * 1024 + ((x + 1024) % 1024)) * 3;
                px[o] = r; px[o + 1] = g; px[o + 2] = b;
            }
        }
        Disc(300, 150, 144, 255, 190, 120);   // glow
        Disc(300, 150, 9, 255, 255, 255);     // white lamp
        Disc(700, 200, 100, 255, 200, 110);   // glow
        Disc(700, 200, 4, 255, 254, 185);     // yellow core
        Disc(900, 100, 0, 255, 255, 255);     // a single-pixel speck
        return px;
    }

    private const string EstimateGoldenJson =
        """
        {"version":2,"estimated":2,"key":{"s":0.29345703,"t":0.29396018,"r":1.3767703,"g":0.9194592,"b":0.6883851},"lamps":[{"s":0.68408203,"t":0.39160424,"r":0.69489634,"g":0.4990029,"b":0.34744817}],"regions":[{"s":0.25072125,"t":0.1682944,"r":0.17136969,"g":0.36895105,"b":1.0865415},{"s":0.74998695,"t":0.16692786,"r":0.16168451,"g":0.36598167,"b":1.09236},{"s":0.7490752,"t":0.47458154,"r":0.24698937,"g":0.357289,"b":0.8621615},{"s":0.2500115,"t":0.47587448,"r":0.23904935,"g":0.35443312,"b":0.867668}],"total":{"r":0.81909287,"g":1.4466548,"b":3.9087312}}
        """;

    [Fact] // [EDGE-ldr-light-estimate-35]
    public void EstimateGolden_ValuesWithinTolerance()
    {
        var px = EstimateGoldenSource(out var w, out var h);
        var actual = LightRigStore.Serialise(LdrLightEstimator.Analyse(RadianceProbe.BuildSrgb8(px, w, h)).Rig);
        AssertJsonClose(EstimateGoldenJson, actual,
            "LdrLightEstimator's output changed. If the change is intended, bump LdrLightEstimator.Version, regenerate the bundled "
            + "light rigs (PrepareBackdrops rigs) and capture this golden again.");
    }

    /// <summary>Same structure, every number within 1e-6 relative (absolute floor 1e-9).</summary>
    internal static void AssertJsonClose(string expected, string actual, string message)
    {
        var e = new List<string>();
        var a = new List<string>();
        Flatten(JsonNode.Parse(expected), "", e);
        Flatten(JsonNode.Parse(actual), "", a);
        Assert.True(e.Count == a.Count, $"{message}\nActual: {actual}");
        for (var i = 0; i < e.Count; i++)
        {
            var (ep, ev) = Split(e[i]);
            var (ap, av) = Split(a[i]);
            Assert.True(ep == ap, $"{message}\nPath {ep} vs {ap}\nActual: {actual}");
            if (double.TryParse(ev, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                && double.TryParse(av, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                Assert.True(Math.Abs(x - y) <= Math.Max(1e-9, 1e-6 * Math.Abs(x)), $"{message}\n{ep}: {x:R} vs {y:R}\nActual: {actual}");
            else Assert.True(ev == av, $"{message}\n{ep}: {ev} vs {av}\nActual: {actual}");
        }
    }

    private static (string, string) Split(string s)
    {
        var i = s.IndexOf('=');
        return (s[..i], s[(i + 1)..]);
    }

    private static void Flatten(JsonNode? n, string path, List<string> into)
    {
        switch (n)
        {
            case JsonObject o:
                foreach (var (k, v) in o) Flatten(v, path + "." + k, into);
                break;
            case JsonArray arr:
                into.Add(path + "#=" + arr.Count);
                for (var i = 0; i < arr.Count; i++) Flatten(arr[i], path + "[" + i + "]", into);
                break;
            case null:
                into.Add(path + "=null");
                break;
            default:
                into.Add(path + "=" + n.ToJsonString());
                break;
        }
    }
}
