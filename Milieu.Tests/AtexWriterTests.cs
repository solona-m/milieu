using System;
using System.IO;
using System.Linq;
using Milieu.GameFiles;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public class AtexWriterTests
{
    [Fact] // [AC-hdri-backdrop-6]
    public void Layout_8192_MatchesReferenceSize()
    {
        var l = AtexWriter.Layout(8192, 4096);
        Assert.Equal(11, l.MipCount);
        Assert.Equal(new uint[] { 80, 33554512, 41943120, 44040272, 44564560, 44695632, 44728400, 44736592, 44738640, 44739152, 44739280, 0, 0 },
                     l.Offsets);
        Assert.Equal(44739312, l.TotalLength);
        Assert.Equal(8, l.MipWidths[^1]);
        Assert.Equal(4, l.MipHeights[^1]);

        Assert.Equal(10, AtexWriter.Layout(4096, 2048).MipCount);
        Assert.Equal(8, AtexWriter.Layout(1024, 512).MipCount);
    }

    [Fact] // [AC-hdri-backdrop-6]
    public void Write_ParsesBack()
    {
        var l = AtexWriter.Layout(16, 8);
        Assert.Equal(new[] { 128, 32 }, l.MipBytes);   // 4x2 blocks, then 2x1
        var mips = l.MipBytes.Select((n, i) => Enumerable.Repeat((byte)(i + 1), n).ToArray()).ToList();
        using var ms = new MemoryStream();
        AtexWriter.Write(ms, 16, 8, mips);
        var b = ms.ToArray();
        Assert.Equal(l.TotalLength, b.Length);
        Assert.Equal(80 + 128 + 32, b.Length);
        Assert.Equal(0x00800000u, BitConverter.ToUInt32(b, 0));
        Assert.Equal(0x6432u, BitConverter.ToUInt32(b, 4));
        Assert.Equal(16, BitConverter.ToUInt16(b, 8));
        Assert.Equal(8, BitConverter.ToUInt16(b, 10));
        Assert.Equal(1, BitConverter.ToUInt16(b, 12));
        Assert.Equal(2, b[14]);
        Assert.Equal(0, b[15]);
        Assert.Equal(80u, BitConverter.ToUInt32(b, 28));
        Assert.Equal(208u, BitConverter.ToUInt32(b, 32));
        Assert.Equal(1, b[80]);
        Assert.Equal(2, b[208]);

        Assert.Throws<ArgumentException>(() => AtexWriter.Write(new MemoryStream(), 16, 8, [new byte[128], new byte[31]]));
        Assert.Throws<ArgumentException>(() => AtexWriter.Write(new MemoryStream(), 16, 8, [new byte[128]]));
        Assert.Throws<ArgumentException>(() => AtexWriter.Layout(18, 8));
    }

    [ReferenceFileFact(ReferenceFiles.Atex)] // [AC-hdri-backdrop-6] (reference)
    public void Reference_HeaderMatches()
    {
        var reference = new byte[80];
        using (var f = File.OpenRead(ReferenceFiles.Path(ReferenceFiles.Atex))) f.ReadExactly(reference);
        Assert.Equal(reference, AtexWriter.Header(AtexWriter.Layout(8192, 4096)));
        Assert.Equal(new FileInfo(ReferenceFiles.Path(ReferenceFiles.Atex)).Length, AtexWriter.Layout(8192, 4096).TotalLength);
    }
}
