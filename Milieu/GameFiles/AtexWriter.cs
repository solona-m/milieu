using System;
using System.Collections.Generic;
using System.IO;

namespace Milieu.GameFiles;

/// <summary>Mip sizes and file offsets of a BC7 <c>.atex</c>.</summary>
internal sealed record AtexLayout(int Width, int Height, int[] MipWidths, int[] MipHeights, int[] MipBytes, uint[] Offsets, long TotalLength)
{
    public int MipCount => MipBytes.Length;
}

/// <summary>
/// Writes a BC7 texture in the game's .tex/.atex container: an 80-byte header (attributes 0x00800000, format 0x6432 =
/// BC7, width, height, depth 1, mip count, array size 0, LoD offsets 0/1/2, 13 surface offsets) followed by the mips.
/// The layout reproduces the reference Sky1.atex byte for byte at 8192x4096. Mips stop at height 4 (one block row).
/// </summary>
internal static class AtexWriter
{
    public const int HeaderSize = 80;
    public const uint Attributes = 0x00800000;
    public const uint FormatBc7 = 0x6432;
    public const int MaxMips = 13;

    public static int BlockBytes(int w, int h) => Math.Max(1, w / 4) * Math.Max(1, h / 4) * 16;

    public static AtexLayout Layout(int width, int height)
    {
        if (width < 4 || height < 4 || width % 4 != 0 || height % 4 != 0)
            throw new ArgumentException($"BC7 textures need dimensions that are multiples of 4 (got {width}x{height}).");
        if (width > ushort.MaxValue || height > ushort.MaxValue) throw new ArgumentException("Texture too large.");
        var ws = new List<int>(); var hs = new List<int>(); var sizes = new List<int>();
        int w = width, h = height;
        while (true)
        {
            ws.Add(w); hs.Add(h); sizes.Add(BlockBytes(w, h));
            if (h <= 4 || w <= 4 || ws.Count == MaxMips) break;
            w /= 2; h /= 2;
        }
        var offsets = new uint[MaxMips];
        long pos = HeaderSize;
        for (var i = 0; i < sizes.Count; i++) { offsets[i] = checked((uint)pos); pos += sizes[i]; }
        return new AtexLayout(width, height, ws.ToArray(), hs.ToArray(), sizes.ToArray(), offsets, pos);
    }

    /// <summary>The exact 80-byte header a w×h BC7 texture written by Milieu starts with.</summary>
    public static byte[] ExpectedHeader(int width, int height) => Header(Layout(width, height));

    public static byte[] Header(AtexLayout layout)
    {
        var h = new byte[HeaderSize];
        var s = h.AsSpan();
        BitConverter.TryWriteBytes(s[0..4], Attributes);
        BitConverter.TryWriteBytes(s[4..8], FormatBc7);
        BitConverter.TryWriteBytes(s[8..10], (ushort)layout.Width);
        BitConverter.TryWriteBytes(s[10..12], (ushort)layout.Height);
        BitConverter.TryWriteBytes(s[12..14], (ushort)1);       // depth
        h[14] = (byte)layout.MipCount;
        h[15] = 0;                                              // array size
        BitConverter.TryWriteBytes(s[16..20], 0u);              // LoD offsets
        BitConverter.TryWriteBytes(s[20..24], 1u);
        BitConverter.TryWriteBytes(s[24..28], 2u);
        for (var i = 0; i < MaxMips; i++) BitConverter.TryWriteBytes(s.Slice(28 + i * 4, 4), layout.Offsets[i]);
        return h;
    }

    public static void Write(Stream output, int width, int height, IReadOnlyList<byte[]> bc7Mips)
    {
        var layout = Layout(width, height);
        if (bc7Mips.Count != layout.MipCount)
            throw new ArgumentException($"Expected {layout.MipCount} mips, got {bc7Mips.Count}.");
        for (var i = 0; i < layout.MipCount; i++)
            if (bc7Mips[i].Length != layout.MipBytes[i])
                throw new ArgumentException($"Mip {i} is {bc7Mips[i].Length} bytes, expected {layout.MipBytes[i]}.");
        output.Write(Header(layout));
        foreach (var mip in bc7Mips) output.Write(mip);
    }
}
