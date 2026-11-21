using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;

namespace Milieu.Tests.Support;

/// <summary>BC7 test helpers (fast-bc7): decode, error metrics, synthetic panoramas, structured blocks, timing.</summary>
public static class Bc7Metrics
{
    /// <summary>BC7 blocks → RGBA through BCnEncoder's decoder (an independent reader).</summary>
    public static byte[] Decode(byte[] blocks, int width, int height)
    {
        var colors = new BcDecoder().DecodeRaw(blocks, width, height, CompressionFormat.Bc7);
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < colors.Length; i++)
        {
            rgba[i * 4] = colors[i].r; rgba[i * 4 + 1] = colors[i].g; rgba[i * 4 + 2] = colors[i].b; rgba[i * 4 + 3] = colors[i].a;
        }
        return rgba;
    }

    /// <summary>RGB sum of squared errors between two 4x4 RGBA blocks (64 bytes each).</summary>
    public static long BlockSse(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        long s = 0;
        for (var i = 0; i < 16; i++)
            for (var c = 0; c < 3; c++)
            {
                var d = a[i * 4 + c] - b[i * 4 + c];
                s += d * d;
            }
        return s;
    }

    /// <summary>RGB PSNR in dB (infinity when identical).</summary>
    public static double Psnr(byte[] src, byte[] decoded, int width, int height)
    {
        double sse = 0;
        for (var i = 0; i < width * height; i++)
            for (var c = 0; c < 3; c++)
            {
                var d = src[i * 4 + c] - decoded[i * 4 + c];
                sse += d * d;
            }
        var mse = sse / (width * (double)height * 3);
        return mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    /// <summary>Every block's RGB MSE (per channel sample) in a decoded image.</summary>
    public static List<double> BlockMses(byte[] src, byte[] decoded, int width, int height)
    {
        var list = new List<double>(width * height / 16);
        for (var by = 0; by < height; by += 4)
            for (var bx = 0; bx < width; bx += 4)
            {
                double s = 0;
                for (var y = 0; y < 4; y++)
                    for (var x = 0; x < 4; x++)
                    {
                        var p = ((by + y) * width + bx + x) * 4;
                        for (var c = 0; c < 3; c++) { var d = src[p + c] - decoded[p + c]; s += d * d; }
                    }
                list.Add(s / 48);
            }
        return list;
    }

    public static double Percentile(List<double> values, double q)
    {
        var sorted = new List<double>(values);
        sorted.Sort();
        var idx = (int)Math.Ceiling(q * sorted.Count) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }

    /// <summary>A 4x4 block of an RGBA image (64 bytes).</summary>
    public static byte[] BlockAt(byte[] rgba, int width, int bx, int by)
    {
        var b = new byte[64];
        for (var y = 0; y < 4; y++) Array.Copy(rgba, ((by * 4 + y) * width + bx * 4) * 4, b, y * 16, 16);
        return b;
    }

    // ------------------------------------------------------------ synthetic panoramas

    public static readonly string[] SyntheticKinds = ["sky", "night", "foliage", "shapes"];

    /// <summary>A synthetic RGBA panorama of the given kind (seeded, deterministic).</summary>
    public static byte[] Synthetic(string kind, int width, int height, int seed = 7)
    {
        var rnd = new Random(seed);
        var px = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                double r, g, b;
                switch (kind)
                {
                    case "sky":
                        {
                            var t = y / (double)height;
                            r = 70 + 120 * t + rnd.Next(-1, 2);
                            g = 120 + 80 * t + rnd.Next(-1, 2);
                            b = 230 - 40 * t + rnd.Next(-1, 2);
                            var sun = Math.Exp(-(Math.Pow(x - width * 0.3, 2) + Math.Pow(y - height * 0.25, 2)) / (width * 2.0));
                            r += 60 * sun; g += 50 * sun; b += 10 * sun;
                            break;
                        }
                    case "night":
                        {
                            var t = y / (double)height;
                            r = 2 + 10 * t; g = 4 + 14 * t; b = 10 + 30 * t;
                            if (rnd.Next(400) == 0) { r = g = b = 40; }
                            r += rnd.Next(0, 2); g += rnd.Next(0, 2); b += rnd.Next(0, 2);
                            break;
                        }
                    case "foliage":
                        {
                            var n = Math.Sin(x * 0.37) * Math.Cos(y * 0.29) + Math.Sin((x + y) * 0.11);
                            r = 40 + 30 * n + rnd.Next(-20, 21);
                            g = 90 + 50 * n + rnd.Next(-25, 26);
                            b = 30 + 20 * n + rnd.Next(-15, 16);
                            break;
                        }
                    default: // shapes: flat colours, hard edges, thin strokes ("lettering")
                        {
                            r = 200; g = 210; b = 230;
                            if ((x / 23 + y / 17) % 3 == 0) { r = 180; g = 40; b = 30; }
                            if (Math.Abs(x - y * 2) % 41 < 3) { r = 10; g = 10; b = 10; }
                            if ((x * x + y * y) % 997 < 60) { r = 250; g = 240; b = 20; }
                            break;
                        }
                }
                px[i] = (byte)Math.Clamp((int)Math.Round(r), 0, 255);
                px[i + 1] = (byte)Math.Clamp((int)Math.Round(g), 0, 255);
                px[i + 2] = (byte)Math.Clamp((int)Math.Round(b), 0, 255);
                px[i + 3] = 255;
            }
        return px;
    }

    // ------------------------------------------------------------ structured blocks

    private static readonly byte[] Extremes = [0, 1, 254, 255];

    /// <summary>
    /// A seeded structured block (EDGE-fast-bc7-16): 1-3 distinct colours laid out by a random partition mask, sometimes a
    /// constant channel, all-equal subsets, and channel values drawn from {0, 1, 254, 255} mixed with random ones.
    /// </summary>
    public static byte[] StructuredBlock(Random rnd)
    {
        var colours = rnd.Next(1, 4);
        var palette = new byte[colours * 3];
        for (var i = 0; i < palette.Length; i++)
            palette[i] = rnd.Next(3) == 0 ? Extremes[rnd.Next(4)] : (byte)rnd.Next(256);
        if (rnd.Next(3) == 0)
        {
            var ch = rnd.Next(3);
            var v = palette[ch];
            for (var k = 0; k < colours; k++) palette[k * 3 + ch] = v;
        }
        var mask = rnd.Next(1 << 16);
        var mask2 = rnd.Next(1 << 16);
        var b = new byte[64];
        for (var i = 0; i < 16; i++)
        {
            var k = colours == 1 ? 0 : ((mask >> i) & 1) == 0 ? 0 : colours == 3 && ((mask2 >> i) & 1) != 0 ? 2 : 1;
            b[i * 4] = palette[k * 3]; b[i * 4 + 1] = palette[k * 3 + 1]; b[i * 4 + 2] = palette[k * 3 + 2]; b[i * 4 + 3] = 255;
        }
        return b;
    }

    public static byte[] RandomBlock(Random rnd)
    {
        var b = new byte[64];
        rnd.NextBytes(b);
        for (var i = 3; i < 64; i += 4) b[i] = 255;
        return b;
    }

    // ------------------------------------------------------------ timing

    /// <summary>One warm-up run, then the best wall time of <paramref name="runs"/>.</summary>
    public static async Task<TimeSpan> BestOf(int runs, Func<Task> action)
    {
        await action();
        var best = TimeSpan.MaxValue;
        for (var i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            await action();
            sw.Stop();
            if (sw.Elapsed < best) best = sw.Elapsed;
        }
        return best;
    }
}
