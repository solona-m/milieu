using System;
using System.Collections.Generic;
using System.IO;
using Milieu.Imaging;
using StbImageSharp;
using StbImageWriteSharp;

namespace PrepareBackdrops;

/// <summary>
/// Prepares the bundled backdrops. Reads only from srcDir and writes only into dstDir.
/// Equirect: fit to 8192x4096 (copied byte for byte when it already fits). Strip (a non-wrapping panorama such as
/// Sunset): scaled to 4096 wide, mirror-tiled to 8192 so it wraps, its horizon placed on the equator, and the sky and
/// ground fade-filled. Every output must pass the seam detector or nothing is written for it.
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

    private static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("usage: PrepareBackdrops <srcDir> <dstDir>"); return 2; }
        var (src, dst) = (args[0], args[1]);
        Directory.CreateDirectory(dst);
        var failed = 0;
        foreach (var plan in Plans)
        {
            var input = Path.Combine(src, plan.File);
            var output = Path.Combine(dst, plan.File);
            if (!File.Exists(input)) { Console.Error.WriteLine($"missing: {plan.File}"); failed++; continue; }
            ImageResult img;
            using (var s = File.OpenRead(input)) img = ImageResult.FromStream(s, StbImageSharp.ColorComponents.RedGreenBlue);
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
            var rgb = new byte[(long)w * h * 3];
            for (long i = 0, j = 0; i < rgb.Length; i += 3, j += 4) { rgb[i] = rgba[j]; rgb[i + 1] = rgba[j + 1]; rgb[i + 2] = rgba[j + 2]; }
            using (var o = File.Create(output)) new ImageWriter().WriteJpg(rgb, w, h, StbImageWriteSharp.ColorComponents.RedGreenBlue, o, Quality);
            Console.WriteLine($"{plan.File}: {img.Width}x{img.Height} -> {w}x{h} ({plan.Mode}), seam {m.Seam:0.0} vs {m.Adjacent:0.0}");
        }
        return failed == 0 ? 0 : 1;
    }
}
