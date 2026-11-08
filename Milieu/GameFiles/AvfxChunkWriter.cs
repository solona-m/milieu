using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Milieu.GameFiles;

/// <summary>
/// Writes the AVFX chunk format: a 4-byte tag stored as reversed ASCII and zero-padded (<c>"Ver"</c> → <c>72 65 56 00</c>),
/// an int32 content size, the content, then zero padding to a 4-byte boundary. Containers are chunks whose content is
/// more chunks; their size is back-patched when the chunk ends.
/// Format source: VFXEditor <c>Formats/AvfxFormat/Base/AvfxBase.Utils.cs</c> (WriteAvfxName, CalculatePadding).
/// </summary>
internal sealed class AvfxChunkWriter
{
    private readonly MemoryStream stream = new();
    private readonly BinaryWriter writer;
    private readonly Stack<long> open = new();

    public AvfxChunkWriter() => writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

    public static byte[] EncodeTag(string tag)
    {
        if (tag.Length is < 1 or > 4) throw new ArgumentException($"AVFX tag '{tag}' must be 1-4 characters.", nameof(tag));
        var bytes = new byte[4];
        for (var i = 0; i < tag.Length; i++)
        {
            var ch = tag[tag.Length - 1 - i];
            if (ch > 0x7E || ch < 0x20) throw new ArgumentException($"AVFX tag '{tag}' must be printable ASCII.", nameof(tag));
            bytes[i] = (byte)ch;
        }
        return bytes;
    }

    public static int Padding(int size) => size % 4 == 0 ? 0 : 4 - size % 4;

    public AvfxChunkWriter Begin(string tag)
    {
        writer.Write(EncodeTag(tag));
        open.Push(stream.Position);
        writer.Write(0);
        return this;
    }

    public AvfxChunkWriter End()
    {
        if (open.Count == 0) throw new InvalidOperationException("End() without a matching Begin().");
        var sizePos = open.Pop();
        var end = stream.Position;
        var size = checked((int)(end - sizePos - 4));
        stream.Position = sizePos;
        writer.Write(size);
        stream.Position = end;
        Pad(size);
        return this;
    }

    public AvfxChunkWriter Int(string tag, int value) => Leaf(tag, BitConverter.GetBytes(value));

    public AvfxChunkWriter Float(string tag, float value) => Leaf(tag, BitConverter.GetBytes(value));

    public AvfxChunkWriter Byte(string tag, byte value) => Leaf(tag, [value]);

    public AvfxChunkWriter Bytes(string tag, ReadOnlySpan<byte> data) => Leaf(tag, data);

    /// <summary>An ASCII string followed by a NUL, as the Tex chunk stores its game path.</summary>
    public AvfxChunkWriter CString(string tag, string value)
    {
        var bytes = new byte[value.Length + 1];
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] > 0x7E || value[i] < 0x20) throw new ArgumentException("AVFX strings must be printable ASCII.", nameof(value));
            bytes[i] = (byte)value[i];
        }
        return Leaf(tag, bytes);
    }

    private AvfxChunkWriter Leaf(string tag, ReadOnlySpan<byte> data)
    {
        writer.Write(EncodeTag(tag));
        writer.Write(data.Length);
        writer.Write(data);
        Pad(data.Length);
        return this;
    }

    private void Pad(int size)
    {
        for (var i = Padding(size); i > 0; i--) writer.Write((byte)0);
    }

    public byte[] ToArray()
    {
        if (open.Count != 0) throw new InvalidOperationException($"{open.Count} chunk(s) still open.");
        writer.Flush();
        return stream.ToArray();
    }
}
