using System;
using System.IO;

namespace Milieu.Imaging;

/// <summary>Reads width, height, bit depth and components straight from PNG (IHDR) and JPEG (SOFn) headers.</summary>
public static class HeaderProbe
{
    public readonly record struct Header(int Width, int Height, int BitsPerChannel, int Components, bool Progressive);

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static Header? Png(Stream s)
    {
        var b = new byte[33];
        if (s.Read(b, 0, b.Length) < 26) return null;
        for (var i = 0; i < 8; i++) if (b[i] != PngSignature[i]) return null;
        if (b[12] != 'I' || b[13] != 'H' || b[14] != 'D' || b[15] != 'R') return null;
        var w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
        var h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
        int depth = b[24], type = b[25];
        var comps = type switch { 0 => 1, 2 => 3, 3 => 3, 4 => 2, 6 => 4, _ => 0 };
        if (w <= 0 || h <= 0 || comps == 0 || depth is not (1 or 2 or 4 or 8 or 16)) return null;
        return new Header(w, h, depth == 16 ? 16 : 8, comps, false);
    }

    public static Header? Jpeg(Stream s)
    {
        try
        {
            if (s.ReadByte() != 0xFF || s.ReadByte() != 0xD8) return null;
            while (true)
            {
                int b;
                do { b = s.ReadByte(); if (b < 0) return null; } while (b != 0xFF);
                int marker;
                do { marker = s.ReadByte(); if (marker < 0) return null; } while (marker == 0xFF);
                if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) continue;
                if (marker is 0xDA or 0xD9) return null;
                int hi = s.ReadByte(), lo = s.ReadByte();
                if (hi < 0 || lo < 0) return null;
                var len = (hi << 8) | lo;
                if (len < 2) return null;
                if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
                {
                    var sof = new byte[6];
                    if (len < 8 || s.Read(sof, 0, 6) < 6) return null;
                    var h = (sof[1] << 8) | sof[2];
                    var w = (sof[3] << 8) | sof[4];
                    if (w <= 0 || h <= 0) return null;
                    var progressive = marker is 0xC2 or 0xC6 or 0xCA or 0xCE;
                    return new Header(w, h, sof[0] > 8 ? 16 : 8, sof[5], progressive);
                }
                if (s.CanSeek)
                {
                    if (s.Position + len - 2 > s.Length) return null;
                    s.Seek(len - 2, SeekOrigin.Current);
                }
                else for (var i = 0; i < len - 2; i++) if (s.ReadByte() < 0) return null;
            }
        }
        catch (IOException) { return null; }
    }
}
