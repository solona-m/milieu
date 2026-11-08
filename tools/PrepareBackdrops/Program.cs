using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Milieu.Assets;
using Milieu.Backdrop;
using Milieu.Imaging;
using StbImageSharp;
using StbImageWriteSharp;

namespace PrepareBackdrops;

/// <summary>
/// Offline tool for the bundled backdrops. It links Milieu's own imaging and conversion code, so what it produces is
/// exactly what the plugin would. Commands (each reads only its input directory and writes only its output directory):
/// <list type="bullet">
/// <item><c>&lt;srcDir&gt; &lt;dstDir&gt;</c>: source panoramas → the committed 8192x4096 JPEGs (Equirect fit, or the Sunset strip).</item>
/// <item><c>atex &lt;jpegDir&gt; &lt;outDir&gt;</c>: committed JPEGs → pre-converted 8192x4096 BC7 .atex; prints the pin lines.</item>
/// <item><c>verify &lt;atexDir&gt; --tag &lt;tag&gt;</c>: checks the .atex set against <see cref="BackdropPins"/>.</item>
/// <item><c>thumbs &lt;jpegDir&gt; &lt;outDir&gt;</c>: 256x128 JPEG previews embedded in the plugin.</item>
/// </list>
/// </summary>
internal static class Program
{
    private enum Mode { Equirect, Strip }

    private sealed record Plan(string File, Mode Mode, double HorizonFraction = 0);

    private static readonly Plan[] Plans =
    [
        new("forest.jpeg", Mode.Equirect),
        new("galaxy.jpeg", Mode.Equirect),
        new("livingroom.jpeg", Mode.Equirect),
        new("livingroom2.jpeg", Mode.Equirect),
        new("lodge.jpeg", Mode.Equirect),
        new("moon.jpeg", Mode.Equirect),
        new("sunset.jpeg", Mode.Strip, 0.9),
        new("warehouse.jpeg", Mode.Equirect),
    ];

    private const int Quality = 92;
    private const int ThumbQuality = 85;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["atex", var src, var dst] => await Atex(src, dst),
                ["verify", var dir, "--tag", var tag] => Verify(dir, tag),
                ["thumbs", var src, var dst] => Thumbs(src, dst),
                [var src, var dst] when !src.StartsWith('-') => Prepare(src, dst),
                _ => Usage(),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"{e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: PrepareBackdrops <srcDir> <dstDir>\n" +
                                "       PrepareBackdrops atex <jpegDir> <outDir>\n" +
                                "       PrepareBackdrops verify <atexDir> --tag <tag>\n" +
                                "       PrepareBackdrops thumbs <jpegDir> <outDir>");
        return 2;
    }

    private static string Stem(string file) => Path.GetFileNameWithoutExtension(file);

    private static ImageResult LoadRgb(string path)
    {
        using var s = File.OpenRead(path);
        return ImageResult.FromStream(s, StbImageSharp.ColorComponents.RedGreenBlue);
    }

    private static byte[] RgbaToRgb(byte[] rgba)
    {
        var rgb = new byte[rgba.Length / 4 * 3];
        for (long i = 0, j = 0; i < rgb.Length; i += 3, j += 4) { rgb[i] = rgba[j]; rgb[i + 1] = rgba[j + 1]; rgb[i + 2] = rgba[j + 2]; }
        return rgb;
    }

    /// <summary>Source panoramas → committed JPEGs.</summary>
    private static int Prepare(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        var failed = 0;
        foreach (var plan in Plans)
        {
            var input = Path.Combine(src, plan.File);
            var output = Path.Combine(dst, plan.File);
            if (!File.Exists(input)) { Console.Error.WriteLine($"missing: {plan.File}"); failed++; continue; }
            var img = LoadRgb(input);
            byte[] rgba; int w, h;
            if (plan.Mode == Mode.Equirect)
            {
                var (tw, th, copy) = BackdropSizing.TargetSize(img.Width, img.Height);
                if (copy)
                {
                    if (SeamDetector.Measure(img.Data, img.Width, img.Height).NoWrap) { Console.Error.WriteLine($"{plan.File}: does not wrap"); failed++; continue; }
                    File.Copy(input, output, overwrite: true);
                    Console.WriteLine($"{plan.File}: copied {img.Width}x{img.Height}");
                    continue;
                }
                rgba = EquirectResampler.Resample(img.Data, img.Width, img.Height, tw, th);
                (w, h) = (tw, th);
            }
            else
            {
                var p = BackdropSizing.PlanStrip(img.Width, img.Height, plan.HorizonFraction);
                var scaled = EquirectResampler.Resample(img.Data, img.Width, img.Height, p.ScaledWidth, p.ScaledHeight);
                var tiled = BackdropSizing.MirrorTile(scaled, p.ScaledWidth, p.ScaledHeight, 4);
                rgba = new byte[(long)p.OutWidth * p.OutHeight * 4];
                Buffer.BlockCopy(tiled, 0, rgba, p.Top * p.OutWidth * 4, tiled.Length);
                BandFill.Fill(rgba, p.OutWidth, p.OutHeight, 4, p.Top, p.ScaledHeight, 512);
                (w, h) = (p.OutWidth, p.OutHeight);
            }
            var m = SeamDetector.Measure(rgba, w, h, 4);
            if (m.NoWrap) { Console.Error.WriteLine($"{plan.File}: output does not wrap (seam {m.Seam:0.0} vs {m.Adjacent:0.0}); not written"); failed++; continue; }
            using (var o = File.Create(output)) new ImageWriter().WriteJpg(RgbaToRgb(rgba), w, h, StbImageWriteSharp.ColorComponents.RedGreenBlue, o, Quality);
            Console.WriteLine($"{plan.File}: {img.Width}x{img.Height} -> {w}x{h} ({plan.Mode}), seam {m.Seam:0.0} vs {m.Adjacent:0.0}");
        }
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Committed JPEGs → pre-converted .atex, with the same conversion code the plugin uses.</summary>
    private static async Task<int> Atex(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        var encoder = new Bc7();
        foreach (var plan in Plans)
        {
            var stem = Stem(plan.File);
            var img = LoadRgb(Path.Combine(src, plan.File));
            var (outW, outH) = BackdropSizing.OutputSize(img.Width, BackdropPins.Width);
            var output = Path.Combine(dst, stem + ".atex");
            var started = DateTime.UtcNow;
            await using (var fs = File.Create(output))
                await TextureConversion.ConvertToAtexAsync(img.Data, img.Width, img.Height, outW, outH, encoder, fs);
            var bytes = new FileInfo(output).Length;
            string sha;
            await using (var fs = File.OpenRead(output)) sha = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
            var display = BackdropPins.Find(stem)?.DisplayName ?? stem;
            Console.WriteLine($"        new(\"{stem}\", \"{display}\", {bytes}L, \"{sha}\"),   // {(DateTime.UtcNow - started).TotalSeconds:0}s");
        }
        return 0;
    }

    private static int Verify(string dir, string tag)
    {
        var problems = BackdropPins.Verify(dir, tag);
        foreach (var p in problems) Console.Error.WriteLine(p);
        if (problems.Count == 0) Console.WriteLine($"{BackdropPins.Count} files match the pins for {tag}.");
        return problems.Count == 0 ? 0 : 1;
    }

    /// <summary>Committed JPEGs → 256x128 previews (Milieu/Resources/Thumbs/&lt;stem&gt;.jpg).</summary>
    private static int Thumbs(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var plan in Plans)
        {
            var img = LoadRgb(Path.Combine(src, plan.File));
            var rgba = EquirectResampler.Resample(img.Data, img.Width, img.Height, TextureConversion.ThumbWidth, TextureConversion.ThumbHeight);
            var output = Path.Combine(dst, Stem(plan.File) + ".jpg");
            using (var o = File.Create(output))
                new ImageWriter().WriteJpg(RgbaToRgb(rgba), TextureConversion.ThumbWidth, TextureConversion.ThumbHeight,
                                           StbImageWriteSharp.ColorComponents.RedGreenBlue, o, ThumbQuality);
            Console.WriteLine($"{Path.GetFileName(output)}: {new FileInfo(output).Length} bytes");
        }
        return 0;
    }
}
