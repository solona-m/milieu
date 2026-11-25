using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Milieu.Assets;
using Milieu.Backdrop;
using Milieu.Imaging;
using Milieu.Lighting;
using StbImageSharp;
using StbImageWriteSharp;

namespace PrepareBackdrops;

/// <summary>
/// Offline tool for the bundled backdrops. It links Milieu's own imaging and conversion code, so what it produces is
/// exactly what the plugin would. Commands (each reads only its input directory and writes only its output directory):
/// <list type="bullet">
/// <item><c>&lt;srcDir&gt; &lt;dstDir&gt;</c>: source panoramas → the committed 8192x4096 JPEGs (Equirect fit, or the Sunset strip).
/// A Radiance <c>.hdr</c> source is tone-mapped with the plugin's default exposure and operator first, never copied.</item>
/// <item><c>atex &lt;jpegDir&gt; &lt;outDir&gt;</c>: committed JPEGs → pre-converted 8192x4096 BC7 .atex; prints the pin lines.</item>
/// <item><c>verify &lt;atexDir&gt; --tag &lt;tag&gt;</c>: checks the .atex set against <see cref="BackdropPins"/>.</item>
/// <item><c>thumbs &lt;jpegDir&gt; &lt;outDir&gt;</c>: 256x128 JPEG previews embedded in the plugin.</item>
/// <item><c>rigs &lt;jpegDir&gt; &lt;outFile&gt;</c>: committed JPEGs → the bundled light-rig table (a data-only C# partial,
/// docs/adr/0010); writes only <c>&lt;outFile&gt;</c> and prints a per-scene summary.</item>
/// </list>
/// </summary>
internal static class Program
{
    private enum Mode { Equirect, Strip }

    /// <summary><paramref name="File"/> is the committed JPEG; <paramref name="Source"/> the file read from the source
    /// directory when it differs (only <c>Prepare</c> reads it).</summary>
    private sealed record Plan(string File, Mode Mode, double HorizonFraction = 0, string? Source = null)
    {
        public string SourceFile => Source ?? File;
        public bool IsHdr => Path.GetExtension(SourceFile).Equals(".hdr", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Plan[] Plans =
    [
        new("autumn.jpeg", Mode.Equirect),
        new("christmasstudio.jpeg", Mode.Equirect, Source: "Christmas Studio.hdr"),
        new("christmasstudio2.jpeg", Mode.Equirect, Source: "Christmas Studio 2.hdr"),
        new("forest.jpeg", Mode.Equirect),
        new("galaxy.jpeg", Mode.Equirect),
        new("livingroom.jpeg", Mode.Equirect),
        new("livingroom2.jpeg", Mode.Equirect),
        new("lodge.jpeg", Mode.Equirect),
        new("moon.jpeg", Mode.Equirect),
        new("snow.jpeg", Mode.Equirect),
        new("sunset.jpeg", Mode.Strip, 0.9),
        new("warehouse.jpeg", Mode.Equirect),
    ];

    private const int Quality = 92;
    private const int ThumbQuality = 85;

    // The plugin's defaults for an HDR image (BackdropSettings.ExposureEv and ToneMap), so an HDR-sourced bundled scene
    // looks like that .hdr added as a user image with untouched settings.
    private const float HdrExposureEv = 0f;
    private const ToneMapOperator HdrToneMap = ToneMapOperator.Aces;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["atex", var src, var dst] => await Atex(src, dst),
                ["verify", var dir, "--tag", var tag] => Verify(dir, tag),
                ["thumbs", var src, var dst] => Thumbs(src, dst),
                ["rigs", var src, var outFile] => Rigs(src, outFile),
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
                                "       PrepareBackdrops thumbs <jpegDir> <outDir>\n" +
                                "       PrepareBackdrops rigs <jpegDir> <outFile>");
        return 2;
    }

    private static string Stem(string file) => Path.GetFileNameWithoutExtension(file);

    private static ImageResult LoadRgb(string path)
    {
        using var s = File.OpenRead(path);
        return ImageResult.FromStream(s, StbImageSharp.ColorComponents.RedGreenBlue);
    }

    /// <summary>A source panorama as 8-bit RGB: LDR decoded as-is; Radiance HDR decoded to float (as ImageLoader does)
    /// and tone-mapped at the plugin defaults.</summary>
    private static (byte[] Rgb, int Width, int Height) LoadSourceRgb(string path, bool hdr)
    {
        if (!hdr) { var img = LoadRgb(path); return (img.Data, img.Width, img.Height); }
        using var s = File.OpenRead(path);
        var f = ImageResultFloat.FromStream(s, StbImageSharp.ColorComponents.RedGreenBlue);
        return (ToneMapper.ToSrgb8(f.Data, f.Width, f.Height, HdrExposureEv, HdrToneMap), f.Width, f.Height);
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
            var input = Path.Combine(src, plan.SourceFile);
            var output = Path.Combine(dst, plan.File);
            if (!File.Exists(input)) { Console.Error.WriteLine($"missing: {plan.SourceFile}"); failed++; continue; }
            var (data, iw, ih) = LoadSourceRgb(input, plan.IsHdr);
            byte[] pixels; int w, h, channels;
            if (plan.Mode == Mode.Equirect)
            {
                var (tw, th, copy) = BackdropSizing.TargetSize(iw, ih);
                if (copy && !plan.IsHdr)
                {
                    if (SeamDetector.Measure(data, iw, ih).NoWrap) { Console.Error.WriteLine($"{plan.File}: does not wrap"); failed++; continue; }
                    File.Copy(input, output, overwrite: true);
                    Console.WriteLine($"{plan.File}: copied {iw}x{ih}");
                    continue;
                }
                // An HDR source already at the target size is encoded from its tone-mapped pixels, never byte-copied.
                if (copy) (pixels, w, h, channels) = (data, iw, ih, 3);
                else (pixels, w, h, channels) = (EquirectResampler.Resample(data, iw, ih, tw, th), tw, th, 4);
            }
            else
            {
                var p = BackdropSizing.PlanStrip(iw, ih, plan.HorizonFraction);
                var scaled = EquirectResampler.Resample(data, iw, ih, p.ScaledWidth, p.ScaledHeight);
                var tiled = BackdropSizing.MirrorTile(scaled, p.ScaledWidth, p.ScaledHeight, 4);
                pixels = new byte[(long)p.OutWidth * p.OutHeight * 4];
                Buffer.BlockCopy(tiled, 0, pixels, p.Top * p.OutWidth * 4, tiled.Length);
                BandFill.Fill(pixels, p.OutWidth, p.OutHeight, 4, p.Top, p.ScaledHeight, 512);
                (w, h, channels) = (p.OutWidth, p.OutHeight, 4);
            }
            var m = SeamDetector.Measure(pixels, w, h, channels);
            if (m.NoWrap) { Console.Error.WriteLine($"{plan.File}: output does not wrap (seam {m.Seam:0.0} vs {m.Adjacent:0.0}); not written"); failed++; continue; }
            var rgb = channels == 4 ? RgbaToRgb(pixels) : pixels;
            using (var o = File.Create(output)) new ImageWriter().WriteJpg(rgb, w, h, StbImageWriteSharp.ColorComponents.RedGreenBlue, o, Quality);
            var how = plan.IsHdr ? $"{plan.Mode}, HDR tone-mapped {HdrToneMap} EV {HdrExposureEv:0}" : plan.Mode.ToString();
            Console.WriteLine($"{plan.SourceFile}: {iw}x{ih} -> {plan.File} {w}x{h} ({how}), seam {m.Seam:0.0} vs {m.Adjacent:0.0}");
        }
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Committed JPEGs → pre-converted .atex, with the same conversion code the plugin uses.</summary>
    private static async Task<int> Atex(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        // The reference encoder on purpose: it produced the pinned published textures (docs/adr/0005, 0016). The plugin
        // encodes user images with the in-repo OpaqueBc7; switching here would change the pins and need a re-publish.
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
    /// <summary>
    /// Committed JPEGs → Milieu/Assets/BundledLightRigs.Data.cs: each scene decoded as the plugin decodes a JPEG (8-bit
    /// RGB), estimated with Milieu's own probe and estimator, each entry tied to the JPEG's and the .atex pin's SHA-256.
    /// </summary>
    private static int Rigs(string src, string outFile)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("// <auto-generated>\n")
          .Append("// Generated by `dotnet run --project tools/PrepareBackdrops -c Release -- rigs Milieu/DefaultBackdrops Milieu/Assets/BundledLightRigs.Data.cs`.\n")
          .Append("// Data only; do not edit. Regenerate when a bundled image, its pin, LightExtractor or LdrLightEstimator changes (docs/adr/0010).\n")
          .Append("// </auto-generated>\n")
          .Append("using System.Collections.Generic;\n\n")
          .Append("namespace Milieu.Assets;\n\n")
          .Append("public static partial class BundledLightRigs\n{\n")
          .Append("    public static readonly IReadOnlyList<BundledRigEntry> All =\n    [\n");
        foreach (var plan in Plans)
        {
            var stem = Stem(plan.File);
            var pin = BackdropPins.Find(stem) ?? throw new InvalidOperationException($"{stem} has no pin");
            var path = Path.Combine(src, plan.File);
            string jpegSha;
            using (var fs = File.OpenRead(path)) jpegSha = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            var img = LoadRgb(path);
            var (rig, stats) = LdrLightEstimator.Analyse(RadianceProbe.BuildSrgb8(img.Data, img.Width, img.Height));
            var json = LightRigStore.Serialise(rig);
            sb.Append($"        new(\"{stem}\", \"{jpegSha}\", \"{pin.Sha256}\",\n            \"\"\"{json}\"\"\"),\n");
            Console.WriteLine(Summary(stem, rig, stats));
        }
        sb.Append("    ];\n}\n");
        File.WriteAllText(outFile, sb.ToString());
        Console.WriteLine($"wrote {Path.GetFileName(outFile)} ({Plans.Length} scenes)");
        return 0;
    }

    private static string Where(RigLight l) =>
        FormattableString.Invariant($"az {(l.S - 0.5) * 360:0.0} el {(0.5 - l.T) * 180:0.0} rgb ({l.Energy.X:0.00},{l.Energy.Y:0.00},{l.Energy.Z:0.00})");

    private static string Summary(string stem, LightRig rig, EstimateStats s) =>
        FormattableString.Invariant($"{stem}: key {(rig.Key is { } k ? Where(k) : "no key")}; lamps {rig.Lamps.Length}")
        + string.Concat(rig.Lamps.Select(l => " [" + Where(l) + "]"))
        + FormattableString.Invariant($"; components {s.Components}, kept {s.Kept}; rejected speck {s.Specks}, broad {s.Broad}, share {s.RejectedShare}, ")
        + FormattableString.Invariant($"outlier {s.RejectedOutlier}, halo {s.RejectedHalo}; fallback {(s.OutlierFallback ? "yes" : "no")}; ")
        + FormattableString.Invariant($"saturated {(s.Saturated ? "yes" : "no")}; clip share {s.ClipShare:0.0000}, strict-white share {s.StrictWhiteShare:0.0000}");
}
