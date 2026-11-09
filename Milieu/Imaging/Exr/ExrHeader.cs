using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Milieu.Imaging.Exr;

public enum ExrPixelType { UInt = 0, Half = 1, Float = 2 }

/// <summary>One chlist entry, in the order the file stores it.</summary>
public sealed record ExrChannel(string Name, ExrPixelType Type, int XSampling, int YSampling)
{
    /// <summary>Bytes per sample: 2 for HALF, 4 for FLOAT and UINT.</summary>
    public int Size => Type == ExrPixelType.Half ? 2 : 4;
}

/// <summary>A fully validated single-part EXR header (scanline or tiled), with the colour channels resolved.</summary>
public sealed record ExrHeaderInfo(
    int Width,
    int Height,
    int XMin,
    int YMin,
    IReadOnlyList<ExrChannel> Channels,
    int[] Selected,
    int Compression,
    bool Tiled,
    int TileWidth,
    int TileHeight,
    int TilesX,
    int LinesPerBlock,
    int ChunkCount,
    long MaxBlockBytes,
    long OffsetTablePosition)
{
    /// <summary>Bytes of one pixel across every stored channel.</summary>
    public int BytesPerPixel => Channels.Sum(c => c.Size);

    /// <summary>16 when every selected channel is HALF, else 32.</summary>
    public int BitsPerChannel => Selected.All(i => Channels[i].Type == ExrPixelType.Half) ? 16 : 32;

    /// <summary>Bytes in a chunk's header before its data: y + size (scanline) or tile x, y, level x, y + size (tiled).</summary>
    public int ChunkHeaderBytes => Tiled ? 20 : 8;
}

/// <summary>Reads and validates the header of an OpenEXR file, rejecting unsupported variants with a specific reason.</summary>
public static class ExrHeader
{
    public const int Magic = 20000630;   // 76 2F 31 01

    private const int FlagTiled = 0x200, FlagLongNames = 0x400, FlagNonImage = 0x800, FlagMultipart = 0x1000;

    private static readonly string[] CompressionNames =
        ["NONE", "RLE", "ZIPS", "ZIP", "PIZ", "PXR24", "B44", "B44A", "DWAA", "DWAB", "HTJ2K256", "HTJ2K32"];

    /// <summary>Lines per scanline chunk for the supported compressions (NONE, RLE, ZIPS, ZIP, PIZ).</summary>
    public static int LinesPerBlockOf(int compression) => compression switch
    {
        0 or 1 or 2 => 1,
        3 => 16,
        4 => 32,
        _ => throw new ArgumentOutOfRangeException(nameof(compression)),
    };

    /// <summary>Reads the header from the stream's current position; leaves the stream at the offset table.</summary>
    public static ExrHeaderInfo Read(Stream s)
    {
        var start = s.Position;
        var length = s.Length;
        Span<byte> b8 = stackalloc byte[8];
        Span<byte> box = stackalloc byte[16];
        ReadExact(s, b8);
        if (BinaryPrimitives.ReadInt32LittleEndian(b8) != Magic) throw Corrupt("not an OpenEXR file");
        var version = BinaryPrimitives.ReadInt32LittleEndian(b8[4..]);
        if ((version & 0xFF) != 2) throw Corrupt("unsupported version");
        var flags = version & ~0xFF;
        if ((flags & ~(FlagTiled | FlagLongNames | FlagNonImage | FlagMultipart)) != 0) throw Corrupt("unknown version flags");
        if ((flags & FlagMultipart) != 0) throw Layout("a multi-part layout");
        if ((flags & FlagNonImage) != 0) throw Layout("deep data");
        var tiled = (flags & FlagTiled) != 0;

        List<ExrChannel>? channels = null;
        int? compression = null, lineOrder = null, envmap = null;
        (int XMin, int YMin, int XMax, int YMax)? dataWindow = null;
        (uint X, uint Y, byte Mode)? tiles = null;

        while (true)
        {
            var name = ReadName(s);
            if (name.Length == 0) break;
            var type = ReadName(s);
            if (type.Length == 0) throw Corrupt("empty attribute type");
            ReadExact(s, b8[..4]);
            var size = BinaryPrimitives.ReadInt32LittleEndian(b8);
            if (size < 0) throw Corrupt("negative attribute size");
            var end = s.Position + size;
            if (end > length) throw Corrupt("attribute past the end of the file");
            if (end - start > ExrLimits.MaxHeaderBytes) throw Corrupt("header too large");

            switch (name)
            {
                case "channels":
                    Expect(type, "chlist", size <= ExrLimits.MaxChlistBytes);
                    var buf = new byte[size];
                    ReadExact(s, buf);
                    channels = ParseChlist(buf);
                    break;
                case "compression":
                    Expect(type, "compression", size == 1);
                    compression = ReadByte(s);
                    break;
                case "dataWindow":
                    Expect(type, "box2i", size == 16);
                    ReadExact(s, box);
                    dataWindow = (BinaryPrimitives.ReadInt32LittleEndian(box), BinaryPrimitives.ReadInt32LittleEndian(box[4..]),
                                  BinaryPrimitives.ReadInt32LittleEndian(box[8..]), BinaryPrimitives.ReadInt32LittleEndian(box[12..]));
                    break;
                case "lineOrder":
                    Expect(type, "lineOrder", size == 1);
                    lineOrder = ReadByte(s);
                    break;
                case "tiles":
                    Expect(type, "tiledesc", size == 9);
                    var td = box[..9];
                    ReadExact(s, td);
                    tiles = (BinaryPrimitives.ReadUInt32LittleEndian(td), BinaryPrimitives.ReadUInt32LittleEndian(td[4..]), td[8]);
                    break;
                case "envmap":
                    Expect(type, "envmap", size == 1);
                    envmap = ReadByte(s);
                    break;
                default:
                    s.Seek(size, SeekOrigin.Current);   // never allocated
                    break;
            }
        }
        if (s.Position - start > ExrLimits.MaxHeaderBytes) throw Corrupt("header too large");

        if (channels == null || compression == null || dataWindow == null || (tiled && tiles == null))
            throw Corrupt("missing a required attribute");

        var dw = dataWindow.Value;
        var w = (long)dw.XMax - dw.XMin + 1;
        var h = (long)dw.YMax - dw.YMin + 1;
        if (w < 1 || h < 1 || w > int.MaxValue || h > int.MaxValue) throw Corrupt("invalid data window");
        if (lineOrder is < 0 or > 2) throw Corrupt("invalid line order");

        int tileW = 0, tileH = 0;
        if (tiled)
        {
            var t = tiles!.Value;
            if (t.X == 0 || t.Y == 0 || t.X > int.MaxValue || t.Y > int.MaxValue) throw Corrupt("invalid tile size");
            if ((t.Mode & 0xF) > 2 || (t.Mode >> 4) > 1) throw Corrupt("invalid tile level mode");
            (tileW, tileH) = ((int)t.X, (int)t.Y);
        }

        var comp = compression.Value;
        if (comp > 4)
            throw new ExrUnsupportedException(ExrUnsupportedKind.Compression,
                                              comp < CompressionNames.Length ? CompressionNames[comp] : $"unknown (id {comp})");

        if (envmap == 1) throw Layout("a cube-map environment");
        if (envmap is not null and not 0) throw Corrupt("invalid envmap");

        var selected = ExrChannelSelector.Select(channels);
        if (channels.Any(c => c.XSampling != 1 || c.YSampling != 1)) throw Layout("subsampled channels");

        var width = (int)w;
        var height = (int)h;
        long bpp = channels.Sum(c => (long)c.Size);
        int linesPerBlock, chunkCount, tilesX = 0;
        long maxBlock;
        if (tiled)
        {
            var tx = (w + tileW - 1) / tileW;
            var ty = (h + tileH - 1) / tileH;
            if (tx * ty > ExrLimits.MaxTiles) throw Layout("very small tiles");
            tilesX = (int)tx;
            chunkCount = (int)(tx * ty);
            linesPerBlock = (int)Math.Min(tileH, h);
            maxBlock = Math.Min(tileW, w) * linesPerBlock * bpp;
        }
        else
        {
            linesPerBlock = LinesPerBlockOf(comp);
            chunkCount = (int)((h + linesPerBlock - 1) / linesPerBlock);
            maxBlock = w * Math.Min(linesPerBlock, h) * bpp;
        }
        if (maxBlock > ExrLimits.MaxBlockBytes) throw Layout("very large tiles or too many channels");

        return new ExrHeaderInfo(width, height, dw.XMin, dw.YMin, channels, selected, comp, tiled, tileW, tileH, tilesX,
                                 linesPerBlock, chunkCount, maxBlock, s.Position);
    }

    private static List<ExrChannel> ParseChlist(byte[] buf)
    {
        var list = new List<ExrChannel>();
        var pos = 0;
        while (true)
        {
            if (pos >= buf.Length) throw Corrupt("channel list has no terminator");
            var nameEnd = Array.IndexOf(buf, (byte)0, pos, Math.Min(buf.Length - pos, ExrLimits.MaxNameBytes + 1));
            if (nameEnd < 0) throw Corrupt("channel name not terminated");
            if (nameEnd == pos)
            {
                if (pos != buf.Length - 1) throw Corrupt("empty channel name");
                break;
            }
            var name = Encoding.Latin1.GetString(buf, pos, nameEnd - pos);
            pos = nameEnd + 1;
            if (buf.Length - pos < 16) throw Corrupt("channel entry truncated");
            var type = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(pos));
            var xs = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(pos + 8));
            var ys = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(pos + 12));
            pos += 16;
            if (type is < 0 or > 2) throw Corrupt("invalid pixel type");
            if (xs < 1 || ys < 1) throw Corrupt("invalid channel sampling");
            if (list.Count == ExrLimits.MaxChannels) throw Corrupt("too many channels");
            list.Add(new ExrChannel(name, (ExrPixelType)type, xs, ys));
        }
        if (list.Count == 0) throw Corrupt("no channels");
        return list;
    }

    private static string ReadName(Stream s)
    {
        Span<byte> name = stackalloc byte[ExrLimits.MaxNameBytes];
        for (var i = 0; ; i++)
        {
            var c = s.ReadByte();
            if (c < 0) throw Corrupt("truncated header");
            if (c == 0) return Encoding.Latin1.GetString(name[..i]);
            if (i == ExrLimits.MaxNameBytes) throw Corrupt("name longer than 255 bytes");
            name[i] = (byte)c;
        }
    }

    private static void Expect(string type, string expected, bool sizeOk)
    {
        if (type != expected || !sizeOk) throw Corrupt($"attribute type {type} is not a valid {expected}");
    }

    private static int ReadByte(Stream s)
    {
        var c = s.ReadByte();
        return c < 0 ? throw Corrupt("truncated header") : c;
    }

    private static void ReadExact(Stream s, Span<byte> buf)
    {
        try { s.ReadExactly(buf); }
        catch (EndOfStreamException) { throw Corrupt("truncated header"); }
    }

    private static ExrFormatException Corrupt(string why) => new(why);

    internal static ExrUnsupportedException Layout(string feature) => new(ExrUnsupportedKind.Layout, feature);
}
