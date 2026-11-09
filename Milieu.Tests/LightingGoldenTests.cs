using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Milieu.Lighting;
using Xunit;

namespace Milieu.Tests;

/// <summary>
/// Goldens that pin the lighting arithmetic (docs/adr/0009, 0010). The probe golden is a SHA-256 of the probe's bytes
/// (plain arithmetic, captured on the code before the LDR estimate work); refactors must keep it.
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
}
