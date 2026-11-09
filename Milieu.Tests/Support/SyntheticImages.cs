using System;
using System.IO;
using System.Text;
using StbImageWriteSharp;

namespace Milieu.Tests.Support;

/// <summary>Builds test images in code (AGENTS.md: synthetic fixtures, no committed binaries).</summary>
public static class SyntheticImages
{
    public static byte[] Rgb(int w, int h, Func<int, int, (byte R, byte G, byte B)> f)
    {
        var px = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var (r, g, b) = f(x, y);
            var o = (y * w + x) * 3;
            px[o] = r; px[o + 1] = g; px[o + 2] = b;
        }
        return px;
    }

    public static byte[] Gradient(int w, int h) =>
        Rgb(w, h, (x, y) => ((byte)(x * 255 / Math.Max(1, w - 1)), (byte)(y * 255 / Math.Max(1, h - 1)), 128));

    public static string WritePng(string dir, string name, int w, int h, byte[]? rgb = null, ColorComponents comp = ColorComponents.RedGreenBlue)
    {
        var path = Path.Combine(dir, name);
        rgb ??= Gradient(w, h);
        var data = comp switch
        {
            ColorComponents.RedGreenBlue => rgb,
            ColorComponents.RedGreenBlueAlpha => ToRgba(rgb, 0),
            ColorComponents.Grey => ToGrey(rgb),
            _ => throw new ArgumentOutOfRangeException(nameof(comp)),
        };
        using var s = File.Create(path);
        new ImageWriter().WritePng(data, w, h, comp, s);
        return path;
    }

    public static string WriteJpg(string dir, string name, int w, int h, byte[]? rgb = null, int quality = 95)
    {
        var path = Path.Combine(dir, name);
        using var s = File.Create(path);
        new ImageWriter().WriteJpg(rgb ?? Gradient(w, h), w, h, ColorComponents.RedGreenBlue, s, quality);
        return path;
    }

    public static string WriteHdr(string dir, string name, int w, int h, Func<int, int, float>? value = null)
    {
        var path = Path.Combine(dir, name);
        var data = new float[w * h * 3];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var v = value?.Invoke(x, y) ?? (x + 1f) / w;
            var o = (y * w + x) * 3;
            data[o] = v; data[o + 1] = v * 0.5f; data[o + 2] = v * 0.25f;
        }
        // Flat (non-RLE) Radiance RGBE, which stb reads for any width.
        using var s = File.Create(path);
        s.Write(Encoding.ASCII.GetBytes($"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {h} +X {w}\n"));
        var px = new byte[4];
        for (var i = 0; i < w * h; i++)
        {
            float r = data[i * 3], g = data[i * 3 + 1], b = data[i * 3 + 2];
            var max = Math.Max(r, Math.Max(g, b));
            if (max < 1e-32f) { px[0] = px[1] = px[2] = px[3] = 0; }
            else
            {
                var e = (int)Math.Ceiling(Math.Log2(max) + 1e-7);
                var scale = 256.0 / Math.Pow(2, e);
                px[0] = (byte)Math.Min(255, r * scale); px[1] = (byte)Math.Min(255, g * scale); px[2] = (byte)Math.Min(255, b * scale);
                px[3] = (byte)(e + 128);
            }
            s.Write(px);
        }
        return path;
    }

    /// <summary>
    /// A float RGB equirect panorama from a function of (azimuth, elevation) in degrees. Pixel centres: azimuth
    /// (s − 0.5)·360 with s = (x+0.5)/w, elevation (0.5 − t)·180 with t = (y+0.5)/h (the image-space convention of
    /// LightExtractor, restated here so the tests don't depend on it).
    /// </summary>
    public static float[] HdrPanorama(int w, int h, Func<double, double, (float R, float G, float B)> f)
    {
        var data = new float[w * h * 3];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var az = ((x + 0.5) / w - 0.5) * 360.0;
            var el = (0.5 - (y + 0.5) / h) * 180.0;
            var (r, g, b) = f(az, el);
            var o = (y * w + x) * 3;
            data[o] = r; data[o + 1] = g; data[o + 2] = b;
        }
        return data;
    }

    /// <summary>The unit direction of (azimuth, elevation) in degrees: (cos el cos az, sin el, cos el sin az).</summary>
    public static System.Numerics.Vector3 Dir(double azDeg, double elDeg)
    {
        double az = azDeg * Math.PI / 180, el = elDeg * Math.PI / 180;
        return new System.Numerics.Vector3((float)(Math.Cos(el) * Math.Cos(az)), (float)Math.Sin(el), (float)(Math.Cos(el) * Math.Sin(az)));
    }

    /// <summary>The angle in degrees between two directions.</summary>
    public static double AngleDeg(System.Numerics.Vector3 a, System.Numerics.Vector3 b) =>
        Math.Acos(Math.Clamp(System.Numerics.Vector3.Dot(System.Numerics.Vector3.Normalize(a), System.Numerics.Vector3.Normalize(b)), -1f, 1f)) * 180 / Math.PI;

    /// <summary>The (azimuth, elevation) in degrees of image coordinates (s,t).</summary>
    public static (double Az, double El) AzEl(double s, double t) => ((s - 0.5) * 360.0, (0.5 - t) * 180.0);

    /// <summary>
    /// A sun disc of the given radius (degrees) at (az, el) over a background: each pixel is supersampled 4x4 so the disc's
    /// rasterised area matches its solid angle closely.
    /// </summary>
    public static float[] SunDisc(int w, int h, double azDeg, double elDeg, double radiusDeg, (float R, float G, float B) sun,
                                  Func<double, double, (float R, float G, float B)> sky)
    {
        var centre = Dir(azDeg, elDeg);
        var cosR = Math.Cos(radiusDeg * Math.PI / 180);
        var data = new float[w * h * 3];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var az0 = ((x + 0.5) / w - 0.5) * 360.0;
            var el0 = (0.5 - (y + 0.5) / h) * 180.0;
            var (r, g, b) = sky(az0, el0);
            var close = System.Numerics.Vector3.Dot(Dir(az0, el0), centre) > Math.Cos((radiusDeg + 2) * Math.PI / 180);
            if (close)
            {
                var hits = 0;
                for (var sy = 0; sy < 4; sy++)
                for (var sx = 0; sx < 4; sx++)
                {
                    var az = ((x + (sx + 0.5) / 4) / w - 0.5) * 360.0;
                    var el = (0.5 - (y + (sy + 0.5) / 4) / h) * 180.0;
                    if (System.Numerics.Vector3.Dot(Dir(az, el), centre) >= cosR) hits++;
                }
                var f = hits / 16f;
                r = r * (1 - f) + sun.R * f; g = g * (1 - f) + sun.G * f; b = b * (1 - f) + sun.B * f;
            }
            var o = (y * w + x) * 3;
            data[o] = r; data[o + 1] = g; data[o + 2] = b;
        }
        return data;
    }

    // ---- 8-bit panoramas for the LDR light estimate (docs/adr/0010) ----

    /// <summary>
    /// The (azimuth, elevation) in degrees of a point (x, y) in pixel units (x = 2.5 is the centre of column 2) of a w x h
    /// panorama. A band image (wider than 2:1 by more than 1%, EquirectLayout) spans 180°·2h/w of elevation.
    /// </summary>
    public static (double Az, double El) PixelAzEl(double x, double y, int w, int h)
    {
        var span = (double)w / h > 2.02 ? 180.0 * 2 * h / w : 180.0;
        return ((x / w - 0.5) * 360.0, (0.5 - y / h) * span);
    }

    /// <summary>An 8-bit RGB panorama from a function of (azimuth, elevation) at pixel centres.</summary>
    public static byte[] LdrPanorama(int w, int h, Func<double, double, (byte R, byte G, byte B)> f)
    {
        var px = new byte[(long)w * h * 3];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var (az, el) = PixelAzEl(x + 0.5, y + 0.5, w, h);
            var (r, g, b) = f(az, el);
            var o = ((long)y * w + x) * 3;
            px[o] = r; px[o + 1] = g; px[o + 2] = b;
        }
        return px;
    }

    /// <summary>
    /// Paints every pixel within <paramref name="radiusDeg"/> of (az, el) with <paramref name="colour"/>: a pixel is
    /// inside when at least half of its 4x4 subsamples are. Only pixels near the disc are tested.
    /// </summary>
    public static byte[] ClippedDisc(byte[] px, int w, int h, double azDeg, double elDeg, double radiusDeg,
                                     (byte R, byte G, byte B)? colour = null)
    {
        var (r, g, b) = colour ?? ((byte)255, (byte)255, (byte)255);
        ForEachNear(w, h, azDeg, elDeg, radiusDeg, (x, y, inside) =>
        {
            if (inside < 0.5) return;
            var o = ((long)y * w + x) * 3;
            px[o] = r; px[o + 1] = g; px[o + 2] = b;
        });
        return px;
    }

    /// <summary>Paints the ring (innerDeg, outerDeg] around (az, el) (pixel centres) with a colour: a glow or a halo.</summary>
    public static byte[] Ring(byte[] px, int w, int h, double azDeg, double elDeg, double innerDeg, double outerDeg, (byte R, byte G, byte B) colour)
    {
        var centre = Dir(azDeg, elDeg);
        double cosIn = Math.Cos(innerDeg * Math.PI / 180), cosOut = Math.Cos(outerDeg * Math.PI / 180);
        var (y0, y1) = RowsNear(h, w, elDeg, outerDeg);
        for (var y = y0; y <= y1; y++)
        for (var x = 0; x < w; x++)
        {
            var (az, el) = PixelAzEl(x + 0.5, y + 0.5, w, h);
            var d = System.Numerics.Vector3.Dot(Dir(az, el), centre);
            if (d < cosIn && d >= cosOut)
            {
                var o = ((long)y * w + x) * 3;
                px[o] = colour.R; px[o + 1] = colour.G; px[o + 2] = colour.B;
            }
        }
        return px;
    }

    /// <summary>A clipped white disc with a glow of <paramref name="glow"/> out to <paramref name="glowDeg"/> beyond its edge.</summary>
    public static byte[] Lamp(byte[] px, int w, int h, double azDeg, double elDeg, double diameterDeg, (byte R, byte G, byte B) glow, double glowDeg = 4)
    {
        Ring(px, w, h, azDeg, elDeg, 0, diameterDeg / 2 + glowDeg, glow);
        return ClippedDisc(px, w, h, azDeg, elDeg, diameterDeg / 2);
    }

    /// <summary>Sets about <paramref name="density"/> of the pixels (chosen by a seeded RNG) to 255: single clipped pixels.</summary>
    public static byte[] WithScatter(byte[] px, int w, int h, int seed, double density)
    {
        var rng = new Random(seed);
        var n = (long)Math.Round((long)w * h * density);
        for (long i = 0; i < n; i++)
        {
            var o = ((long)rng.Next(h) * w + rng.Next(w)) * 3;
            px[o] = px[o + 1] = px[o + 2] = 255;
        }
        return px;
    }

    /// <summary>
    /// <paramref name="count"/> square stars of <paramref name="sizePx"/> x <paramref name="sizePx"/> white pixels on
    /// black, at seeded positions with |elevation| ≤ <paramref name="maxLatDeg"/>, at least <paramref name="minSepDeg"/>
    /// apart and from every point in <paramref name="avoid"/> (by 2x the separation). Returns the image and star centres.
    /// </summary>
    public static (byte[] Px, System.Collections.Generic.List<(double Az, double El)> Stars) StarField(
        int w, int h, int seed, int count, int sizePx, double minSepDeg, double maxLatDeg, params (double Az, double El)[] avoid)
    {
        var px = new byte[(long)w * h * 3];
        var rng = new Random(seed);
        var stars = new System.Collections.Generic.List<(double Az, double El)>();
        var dirs = new System.Collections.Generic.List<System.Numerics.Vector3>();
        var cosSep = Math.Cos(minSepDeg * Math.PI / 180);
        var cosAvoid = Math.Cos(2 * minSepDeg * Math.PI / 180);
        while (stars.Count < count)
        {
            var x0 = rng.Next(w - sizePx);
            var y0 = rng.Next(h - sizePx);
            var (az, el) = PixelAzEl(x0 + sizePx / 2.0, y0 + sizePx / 2.0, w, h);
            if (Math.Abs(el) > maxLatDeg) continue;
            var d = Dir(az, el);
            var ok = true;
            foreach (var o in dirs) if (System.Numerics.Vector3.Dot(o, d) > cosSep) { ok = false; break; }
            foreach (var a in avoid) if (System.Numerics.Vector3.Dot(Dir(a.Az, a.El), d) > cosAvoid) ok = false;
            if (!ok) continue;
            dirs.Add(d);
            stars.Add((az, el));
            for (var y = y0; y < y0 + sizePx; y++)
            for (var x = x0; x < x0 + sizePx; x++)
            {
                var o = ((long)y * w + x) * 3;
                px[o] = px[o + 1] = px[o + 2] = 255;
            }
        }
        return (px, stars);
    }

    /// <summary>
    /// <paramref name="count"/> clipped downlights of <paramref name="sizeDeg"/> across at elevation
    /// <paramref name="elDeg"/>, evenly spaced in azimuth from 0°, each with a warm glow (sRGB 110,85,60, Y ≈ 0.1) out to
    /// 4° beyond its edge, on a dim ceiling (sRGB 39, Y ≈ 0.02). Returns the image and the lamp azimuths.
    /// </summary>
    public static (byte[] Px, double[] Azimuths) Downlights(int w, int h, int count, double sizeDeg, double elDeg)
    {
        var px = LdrPanorama(w, h, (_, _) => (39, 39, 39));
        var az = new double[count];
        for (var i = 0; i < count; i++)
        {
            az[i] = -180 + 360.0 * i / count + 15;
            Lamp(px, w, h, az[i], elDeg, sizeDeg, (110, 85, 60));
        }
        return (px, az);
    }

    private static (int Y0, int Y1) RowsNear(int h, int w, double elDeg, double radiusDeg)
    {
        var span = (double)w / h > 2.02 ? 180.0 * 2 * h / w : 180.0;
        var top = (0.5 - Math.Min(90, elDeg + radiusDeg + 1) / span) * h;
        var bottom = (0.5 - Math.Max(-90, elDeg - radiusDeg - 1) / span) * h;
        return (Math.Clamp((int)Math.Floor(top) - 1, 0, h - 1), Math.Clamp((int)Math.Ceiling(bottom) + 1, 0, h - 1));
    }

    private static void ForEachNear(int w, int h, double azDeg, double elDeg, double radiusDeg, Action<int, int, double> visit)
    {
        var centre = Dir(azDeg, elDeg);
        var cosR = Math.Cos(radiusDeg * Math.PI / 180);
        var cosNear = Math.Cos((radiusDeg + 1.5) * Math.PI / 180);
        var (y0, y1) = RowsNear(h, w, elDeg, radiusDeg);
        for (var y = y0; y <= y1; y++)
        for (var x = 0; x < w; x++)
        {
            var (az0, el0) = PixelAzEl(x + 0.5, y + 0.5, w, h);
            if (System.Numerics.Vector3.Dot(Dir(az0, el0), centre) < cosNear) continue;
            var hits = 0;
            for (var sy = 0; sy < 4; sy++)
            for (var sx = 0; sx < 4; sx++)
            {
                var (az, el) = PixelAzEl(x + (sx + 0.5) / 4, y + (sy + 0.5) / 4, w, h);
                if (System.Numerics.Vector3.Dot(Dir(az, el), centre) >= cosR) hits++;
            }
            visit(x, y, hits / 16.0);
        }
    }

    /// <summary>Writes float RGB as a flat (non-RLE) Radiance .hdr.</summary>
    public static string WriteHdrRgb(string dir, string name, int w, int h, float[] rgb)
    {
        var path = Path.Combine(dir, name);
        using var s = File.Create(path);
        s.Write(Encoding.ASCII.GetBytes($"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {h} +X {w}\n"));
        var px = new byte[4];
        for (var i = 0; i < w * h; i++)
        {
            float r = rgb[i * 3], g = rgb[i * 3 + 1], b = rgb[i * 3 + 2];
            var max = Math.Max(r, Math.Max(g, b));
            if (max < 1e-32f) { px[0] = px[1] = px[2] = px[3] = 0; }
            else
            {
                var e = (int)Math.Ceiling(Math.Log2(max) + 1e-7);
                var scale = 256.0 / Math.Pow(2, e);
                px[0] = (byte)Math.Min(255, r * scale); px[1] = (byte)Math.Min(255, g * scale); px[2] = (byte)Math.Min(255, b * scale);
                px[3] = (byte)(e + 128);
            }
            s.Write(px);
        }
        return path;
    }

    /// <summary>A Radiance header with no pixels: enough for a probe, fails a full decode.</summary>
    public static string WriteHdrHeaderOnly(string dir, string name, int w, int h)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes($"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {h} +X {w}\n"));
        return path;
    }

    /// <summary>A PNG signature + IHDR only (bit depth and colour type given), enough for a probe.</summary>
    public static string WritePngHeaderOnly(string dir, string name, int w, int h, byte bitDepth = 8, byte colourType = 2)
    {
        var path = Path.Combine(dir, name);
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteBe(ihdr, 0, w); WriteBe(ihdr, 4, h);
        ihdr[8] = bitDepth; ihdr[9] = colourType; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(ms, "IHDR", ihdr);
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>SOI + APP0 + a SOF marker (0xC0 baseline, 0xC2 progressive, …), optionally padded with a COM segment.</summary>
    public static byte[] JpegHeader(byte sofMarker, int w, int h, int padToBytes = 0)
    {
        using var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8]);
        ms.Write([0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        ms.Write([0xFF, sofMarker, 0x00, 0x11, 8, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 3,
                  1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1]);
        while (padToBytes > 0 && ms.Length < padToBytes)
        {
            var len = (int)Math.Min(65535, Math.Max(2, padToBytes - ms.Length - 2));
            ms.Write([0xFF, 0xFE, (byte)(len >> 8), (byte)len]);
            ms.Write(new byte[len - 2]);
        }
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; WriteBe(len, 0, data.Length);
        s.Write(len);
        var t = Encoding.ASCII.GetBytes(type);
        s.Write(t); s.Write(data);
        var crc = Crc32(t, data);
        var c = new byte[4]; WriteBe(c, 0, (int)crc);
        s.Write(c);
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var arr in new[] { a, b })
            foreach (var x in arr)
            {
                crc ^= x;
                for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        return crc ^ 0xFFFFFFFFu;
    }

    private static void WriteBe(byte[] b, int o, int v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    private static byte[] ToRgba(byte[] rgb, byte alpha)
    {
        var n = rgb.Length / 3;
        var r = new byte[n * 4];
        for (var i = 0; i < n; i++) { r[i * 4] = rgb[i * 3]; r[i * 4 + 1] = rgb[i * 3 + 1]; r[i * 4 + 2] = rgb[i * 3 + 2]; r[i * 4 + 3] = alpha; }
        return r;
    }

    private static byte[] ToGrey(byte[] rgb)
    {
        var n = rgb.Length / 3;
        var g = new byte[n];
        for (var i = 0; i < n; i++) g[i] = rgb[i * 3];
        return g;
    }
}

/// <summary>A temp directory deleted at the end of the test.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "milieu-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
