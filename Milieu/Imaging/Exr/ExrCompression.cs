using System;
using System.IO;
using System.IO.Compression;

namespace Milieu.Imaging.Exr;

/// <summary>
/// RLE, ZIPS and ZIP chunk decoding (after ImfRle, ImfRleCompressor and ImfZip). Every routine writes into a pre-sized
/// buffer and fails when the input would produce fewer or more bytes than expected.
/// </summary>
public static class ExrCompression
{
    /// <summary>Expands OpenEXR run-length data into exactly <paramref name="dst"/>.Length bytes.</summary>
    public static void RleExpand(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int i = 0, o = 0;
        while (i < src.Length)
        {
            var c = (sbyte)src[i++];
            if (c < 0)
            {
                var n = -c;
                if (src.Length - i < n) throw new ExrFormatException("RLE literal run past the data");
                if (dst.Length - o < n) throw new ExrFormatException("RLE literal run past the output");
                src.Slice(i, n).CopyTo(dst[o..]);
                i += n;
                o += n;
            }
            else
            {
                var n = c + 1;
                if (i >= src.Length) throw new ExrFormatException("RLE repeat run past the data");
                if (dst.Length - o < n) throw new ExrFormatException("RLE repeat run past the output");
                dst.Slice(o, n).Fill(src[i++]);
                o += n;
            }
        }
        if (o != dst.Length) throw new ExrFormatException("RLE data too short");
    }

    /// <summary>Inflates a zlib stream into exactly <paramref name="count"/> bytes of <paramref name="dst"/>; extra output is corrupt.</summary>
    public static void ZlibInflateExact(byte[] src, int srcLength, byte[] dst, int count)
    {
        using var ms = new MemoryStream(src, 0, srcLength, writable: false);
        using var z = new ZLibStream(ms, CompressionMode.Decompress);
        var o = 0;
        while (o < count)
        {
            var n = z.Read(dst, o, count - o);
            if (n == 0) throw new ExrFormatException("zlib data too short");
            o += n;
        }
        if (z.ReadByte() >= 0) throw new ExrFormatException("zlib data too long");
    }

    /// <summary>
    /// The shared RLE/ZIP post-step: undoes the byte predictor in place in <paramref name="data"/>, then un-interleaves its
    /// two halves into <paramref name="dst"/> (output byte 2k from the first half, 2k+1 from the second).
    /// </summary>
    public static void Reconstruct(Span<byte> data, Span<byte> dst)
    {
        if (dst.Length < data.Length) throw new ArgumentException("destination too small", nameof(dst));
        for (var i = 1; i < data.Length; i++) data[i] = (byte)(data[i - 1] + data[i] - 128);
        var half = (data.Length + 1) / 2;
        int t1 = 0, t2 = half, s = 0;
        while (true)
        {
            if (s < data.Length) dst[s++] = data[t1++]; else break;
            if (s < data.Length) dst[s++] = data[t2++]; else break;
        }
    }
}
