using System;
using System.Collections.Generic;
using System.Threading;

namespace Milieu.Imaging;

/// <summary>RGBA mip levels by 2x2 box filter, down to height 4 (one BC7 block row), like the reference texture.</summary>
public static class MipChain
{
    public static IReadOnlyList<(int Width, int Height)> Sizes(int width, int height)
    {
        var list = new List<(int, int)>();
        int w = width, h = height;
        while (true)
        {
            list.Add((w, h));
            if (h <= 4 || w <= 4 || list.Count == 13) break;
            w /= 2; h /= 2;
        }
        return list;
    }

    public static List<byte[]> Build(byte[] rgba, int width, int height, CancellationToken ct = default)
    {
        var sizes = Sizes(width, height);
        var mips = new List<byte[]>(sizes.Count) { rgba };
        for (var i = 1; i < sizes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (pw, _) = sizes[i - 1];
            var (w, h) = sizes[i];
            var prev = mips[i - 1];
            var next = new byte[(long)w * h * 4];
            for (var y = 0; y < h; y++)
            {
                var r0 = (long)(2 * y) * pw * 4;
                var r1 = r0 + pw * 4L;
                var o = (long)y * w * 4;
                for (var x = 0; x < w; x++)
                {
                    var a = r0 + x * 8L;
                    var b = r1 + x * 8L;
                    for (var c = 0; c < 4; c++)
                        next[o + x * 4 + c] = (byte)((prev[a + c] + prev[a + 4 + c] + prev[b + c] + prev[b + 4 + c] + 2) / 4);
                }
            }
            mips.Add(next);
        }
        return mips;
    }
}
