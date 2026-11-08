using System;
using System.IO;
using Milieu.GameFiles;
using Xunit;

namespace Milieu.Tests;

public class AvfxChunkIoTests
{
    [Fact] // [AC-hdri-backdrop-1]
    public void Tag_IsReversedAsciiZeroPadded_AndPaddingTo4()
    {
        Assert.Equal(new byte[] { 0x72, 0x65, 0x56, 0x00 }, AvfxChunkWriter.EncodeTag("Ver"));
        Assert.Equal(new byte[] { (byte)'D', (byte)'F', (byte)'P', (byte)'b' }, AvfxChunkWriter.EncodeTag("bPFD"));

        var bytes = new AvfxChunkWriter()
            .Begin("AVFX")
            .Byte("bAD", 7)
            .Begin("Emit").Int("PrCn", 1).Float("Val", 2.5f).End()
            .CString("Tex", "vfx/a.atex")
            .End()
            .ToArray();

        // 1-byte chunk: 8-byte header + 1 data byte + 3 padding bytes.
        Assert.Equal(7, bytes[16]);
        Assert.Equal(new byte[] { 0, 0, 0 }, bytes[17..20]);
        Assert.Equal(0, bytes.Length % 4);
        Assert.Equal(bytes.Length - 8, BitConverter.ToInt32(bytes, 4));

        var root = AvfxChunkReader.Parse(bytes);
        Assert.Equal(new[] { "bAD", "Emit", "Tex" }, root.Children.ConvertAll(c => c.Tag));
        Assert.Equal(1, root.Need("bAD").Size);
        Assert.Equal(7, root.Need("bAD").AsInt());
        var emit = root.Need("Emit");
        Assert.True(emit.IsContainer);
        Assert.Equal(24, emit.Size);
        Assert.Equal(1, emit.Need("PrCn").AsInt());
        Assert.Equal(2.5f, emit.Need("Val").AsFloat());
        Assert.Equal("vfx/a.atex\0", System.Text.Encoding.ASCII.GetString(root.Need("Tex").Data));
    }

    [Fact] // [AC-hdri-backdrop-1]
    public void Reader_RejectsBrokenSizesAndPadding()
    {
        var good = new AvfxChunkWriter().Begin("AVFX").Byte("bAD", 1).End().ToArray();
        var badSize = (byte[])good.Clone();
        BitConverter.TryWriteBytes(badSize.AsSpan(12, 4), 100);
        Assert.Throws<InvalidDataException>(() => AvfxChunkReader.Parse(badSize));

        var badPad = (byte[])good.Clone();
        badPad[17] = 1;
        Assert.Throws<InvalidDataException>(() => AvfxChunkReader.Parse(badPad));

        var badRoot = (byte[])good.Clone();
        BitConverter.TryWriteBytes(badRoot.AsSpan(4, 4), good.Length);
        Assert.Throws<InvalidDataException>(() => AvfxChunkReader.Parse(badRoot));
    }

    [Fact]
    public void Writer_RejectsUnbalancedAndBadTags()
    {
        Assert.Throws<InvalidOperationException>(() => new AvfxChunkWriter().Begin("AVFX").ToArray());
        Assert.Throws<InvalidOperationException>(() => new AvfxChunkWriter().End());
        Assert.Throws<ArgumentException>(() => AvfxChunkWriter.EncodeTag("TooLong"));
        Assert.Throws<ArgumentException>(() => AvfxChunkWriter.EncodeTag(""));
    }
}
