using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;

namespace Milieu.Imaging.Exr;

/// <summary>
/// Decodes a single-part scanline or tiled (level 0) OpenEXR file to float RGB for its dataWindow, row-major, top row first.
/// The offset table is read and validated before the output is allocated, chunks are streamed one at a time from the
/// file, and every buffer is sized from the header (ADR 0008).
/// </summary>
public static class ExrDecoder
{
    /// <summary>Test seam: invoked with each chunk index before it is decoded. AsyncLocal, so it never leaks across tests.</summary>
    internal static readonly AsyncLocal<Action<int>?> OnChunk = new();

    public static (int Width, int Height, float[] Rgb) Decode(Stream s, CancellationToken ct) => Decode(s, ct, null);

    /// <summary>
    /// Throws only <see cref="ExrFormatException"/>, <see cref="ExrUnsupportedException"/> or
    /// <see cref="OperationCanceledException"/>: truncation and bad zlib data are wrapped as format errors.
    /// </summary>
    internal static (int Width, int Height, float[] Rgb) Decode(Stream s, CancellationToken ct, ExrDecodeTrace? trace)
    {
        try { return DecodeCore(s, ct, trace); }
        catch (EndOfStreamException) { throw new ExrFormatException("file truncated"); }
        catch (InvalidDataException) { throw new ExrFormatException("invalid zlib data"); }
    }

    private static (int Width, int Height, float[] Rgb) DecodeCore(Stream s, CancellationToken ct, ExrDecodeTrace? trace)
    {
        var h = ExrHeader.Read(s);
        if (h.Width > ImageLoader.MaxHdrWidth || h.Height > ImageLoader.MaxHdrHeight)
            throw new ExrFormatException("image larger than the HDR cap");

        var fileLength = s.Length;
        var tableEnd = checked(h.OffsetTablePosition + 8L * h.ChunkCount);
        if (tableEnd > fileLength) throw new ExrFormatException("offset table past the end of the file");
        var offsets = new long[h.ChunkCount];
        var entry = new byte[8];
        for (var i = 0; i < offsets.Length; i++)
        {
            s.ReadExactly(entry);
            var off = BinaryPrimitives.ReadInt64LittleEndian(entry);
            if (off < tableEnd || off > fileLength - h.ChunkHeaderBytes) throw new ExrFormatException("offset table entry out of range");
            offsets[i] = off;
        }

        var maxBlock = (int)h.MaxBlockBytes;
        var rgb = new float[checked((long)h.Width * h.Height * 3)];
        var packed = GC.AllocateUninitializedArray<byte>(maxBlock);
        var unpacked = GC.AllocateUninitializedArray<byte>(maxBlock);
        ushort[]? pizScratch = null;
        ExrPiz? piz = null;
        if (h.Compression == 4)
        {
            pizScratch = GC.AllocateUninitializedArray<ushort>(maxBlock / 2);
            piz = new ExrPiz();
        }

        var bpp = h.BytesPerPixel;
        var sizes = new int[h.Channels.Count];
        for (var c = 0; c < sizes.Length; c++) sizes[c] = h.Channels[c].Size / 2;
        var head = new byte[20];

        for (var i = 0; i < offsets.Length; i++)
        {
            OnChunk.Value?.Invoke(i);
            ct.ThrowIfCancellationRequested();

            s.Position = offsets[i];
            s.ReadExactly(head, 0, h.ChunkHeaderBytes);
            int x0, y0, bw, bh;
            if (h.Tiled)
            {
                int tx = BinaryPrimitives.ReadInt32LittleEndian(head), ty = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4));
                int lx = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(8)), ly = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(12));
                if (lx != 0 || ly != 0 || tx != i % h.TilesX || ty != i / h.TilesX) throw new ExrFormatException("tile coordinates don't match the offset table");
                x0 = tx * h.TileWidth;
                y0 = ty * h.TileHeight;
                bw = Math.Min(h.TileWidth, h.Width - x0);
                bh = Math.Min(h.TileHeight, h.Height - y0);
            }
            else
            {
                var y = BinaryPrimitives.ReadInt32LittleEndian(head);
                y0 = i * h.LinesPerBlock;
                if ((long)y - h.YMin != y0) throw new ExrFormatException("chunk y doesn't match the offset table");
                x0 = 0;
                bw = h.Width;
                bh = Math.Min(h.LinesPerBlock, h.Height - y0);
            }
            var expected = checked(bw * bh * bpp);
            var packedSize = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(h.ChunkHeaderBytes - 4));
            if (packedSize <= 0 || packedSize > expected || packedSize > fileLength - s.Position)
                throw new ExrFormatException("chunk size out of range");
            s.ReadExactly(packed, 0, packedSize);

            byte[] data;
            if (packedSize == expected || h.Compression == 0)
            {
                if (packedSize != expected) throw new ExrFormatException("uncompressed chunk size mismatch");
                data = packed;
            }
            else
            {
                switch (h.Compression)
                {
                    case 1:
                        ExrCompression.RleExpand(packed.AsSpan(0, packedSize), unpacked.AsSpan(0, expected));
                        ExrCompression.Reconstruct(unpacked.AsSpan(0, expected), packed.AsSpan(0, expected));
                        data = packed;
                        break;
                    case 2:
                    case 3:
                        ExrCompression.ZlibInflateExact(packed, packedSize, unpacked, expected);
                        ExrCompression.Reconstruct(unpacked.AsSpan(0, expected), packed.AsSpan(0, expected));
                        data = packed;
                        break;
                    case 4:
                        piz!.Decompress(packed.AsSpan(0, packedSize), unpacked.AsSpan(0, expected), bw, bh, sizes, pizScratch!, trace, ct);
                        data = unpacked;
                        break;
                    default:
                        throw new ExrFormatException("unsupported compression");
                }
            }

            Place(h, data, x0, y0, bw, bh, rgb);
            if (trace != null) trace.Chunks++;
        }
        return (h.Width, h.Height, rgb);
    }

    /// <summary>Converts the selected channels of one block (per line, per stored channel, bw samples) into the output.</summary>
    private static void Place(ExrHeaderInfo h, byte[] data, int x0, int y0, int bw, int bh, float[] rgb)
    {
        var channels = h.Channels;
        var lineBytes = bw * h.BytesPerPixel;
        for (var ly = 0; ly < bh; ly++)
        {
            var lineStart = ly * lineBytes;
            var outRow = ((long)(y0 + ly) * h.Width + x0) * 3;
            for (var k = 0; k < 3; k++)
            {
                var ci = h.Selected[k];
                var at = lineStart;
                for (var c = 0; c < ci; c++) at += bw * channels[c].Size;
                var type = channels[ci].Type;
                var o = outRow + k;
                for (var x = 0; x < bw; x++, o += 3)
                {
                    rgb[o] = type switch
                    {
                        ExrPixelType.Half => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + x * 2))),
                        ExrPixelType.Float => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(at + x * 4)),
                        _ => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + x * 4)),
                    };
                }
            }
        }
    }
}
