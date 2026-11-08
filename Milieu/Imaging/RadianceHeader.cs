using System;
using System.IO;
using System.Text;

namespace Milieu.Imaging;

/// <summary>Reads the size of a Radiance .hdr from its text header ("#?RADIANCE" … blank line … "-Y h +X w").</summary>
public static class RadianceHeader
{
    public static (int Width, int Height)? Read(Stream s)
    {
        var first = ReadLine(s);
        if (first == null || !(first.StartsWith("#?RADIANCE", StringComparison.Ordinal) || first.StartsWith("#?RGBE", StringComparison.Ordinal)))
            return null;
        for (var i = 0; i < 256; i++)
        {
            var line = ReadLine(s);
            if (line == null) return null;
            if (line.Length == 0) break;
            if (line.StartsWith("FORMAT=", StringComparison.Ordinal) && line != "FORMAT=32-bit_rle_rgbe") return null;
        }
        var res = ReadLine(s);
        if (res == null) return null;
        var parts = res.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 || parts[0] != "-Y" || parts[2] != "+X") return null;
        if (!int.TryParse(parts[1], out var h) || !int.TryParse(parts[3], out var w) || w <= 0 || h <= 0) return null;
        return (w, h);
    }

    private static string? ReadLine(Stream s)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 4096; i++)
        {
            var b = s.ReadByte();
            if (b < 0) return sb.Length == 0 ? null : sb.ToString();
            if (b == '\n') return sb.ToString();
            sb.Append((char)b);
        }
        return null;
    }
}
