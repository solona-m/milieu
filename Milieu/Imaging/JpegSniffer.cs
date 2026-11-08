using System.IO;

namespace Milieu.Imaging;

/// <summary>
/// Tells baseline from progressive JPEGs by walking the markers from SOI to the first SOFn. Progressive (SOF2, SOF6,
/// SOF10, SOF14) needs far more decode memory in stb (it keeps every coefficient). Anything unreadable is treated as
/// progressive, the conservative answer for the memory budget; the decode itself reports the error later.
/// </summary>
public static class JpegSniffer
{
    public static bool IsProgressive(Stream s)
    {
        try
        {
            if (s.ReadByte() != 0xFF || s.ReadByte() != 0xD8) return true;
            while (true)
            {
                int b;
                do { b = s.ReadByte(); if (b < 0) return true; } while (b != 0xFF);
                int marker;
                do { marker = s.ReadByte(); if (marker < 0) return true; } while (marker == 0xFF);
                if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) continue;   // markers without a length
                if (marker is 0xC2 or 0xC6 or 0xCA or 0xCE) return true;          // progressive SOFs
                if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC)) return false; // other SOFs
                if (marker is 0xDA or 0xD9) return true;                          // SOS/EOI before any SOF: malformed
                int hi = s.ReadByte(), lo = s.ReadByte();
                if (hi < 0 || lo < 0) return true;
                var len = (hi << 8) | lo;
                if (len < 2) return true;
                if (s.CanSeek)
                {
                    if (s.Position + len - 2 > s.Length) return true;
                    s.Seek(len - 2, SeekOrigin.Current);
                }
                else
                {
                    for (var i = 0; i < len - 2; i++) if (s.ReadByte() < 0) return true;
                }
            }
        }
        catch (IOException) { return true; }
    }

    public static bool IsProgressive(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        return IsProgressive(ms);
    }
}
