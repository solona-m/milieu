using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Milieu.GameFiles;

/// <summary>One chunk of an AVFX file. <see cref="Data"/> is the content without the 8-byte header or padding.</summary>
internal sealed class ChunkNode
{
    public required string Tag { get; init; }
    public required int Offset { get; init; }
    public required int Size { get; init; }
    public required byte[] Data { get; init; }
    public List<ChunkNode> Children { get; } = [];

    public bool IsContainer { get; init; }

    public int AsInt() => Size == 4 ? BitConverter.ToInt32(Data) : Size == 1 ? Data[0] : throw new InvalidDataException($"{Tag} is {Size} bytes, not an int.");

    public float AsFloat() => Size == 4 ? BitConverter.ToSingle(Data) : throw new InvalidDataException($"{Tag} is {Size} bytes, not a float.");

    public ChunkNode? Child(string tag, int index = 0) => Children.Where(c => c.Tag == tag).Skip(index).FirstOrDefault();

    public ChunkNode Need(string tag, int index = 0) =>
        Child(tag, index) ?? throw new InvalidDataException($"{Tag} has no {tag}#{index}.");

    public IEnumerable<ChunkNode> All(string tag) => Children.Where(c => c.Tag == tag);

    /// <summary>The value of a one-key curve: the third float of the first 16-byte key.</summary>
    public float CurveValue()
    {
        var keys = Need("Keys");
        if (keys.Size < 16) throw new InvalidDataException($"{Tag} curve has no key.");
        return BitConverter.ToSingle(keys.Data, 12);
    }
}

/// <summary>
/// Parses AVFX bytes into a <see cref="ChunkNode"/> tree. A chunk is treated as a container when its tag is in the
/// container set for its parent; everything else is a leaf. Used both by the tests and as a safety gate on every
/// generated file before it is written.
/// </summary>
internal static class AvfxChunkReader
{
    /// <summary>Containers in the files Milieu writes (and in the reference's element chain).</summary>
    public static readonly IReadOnlySet<string> DefaultContainers = new HashSet<string>
    {
        "AVFX", "Schd", "Item", "Trgr", "TmLn", "Emit", "ItPr", "Ptcl", "Modl",
        "Life", "CrC", "CrI", "ARs", "Col", "RGB", "A", "Bri", "Pos", "Rot", "Scl", "Scr", "X", "Y", "Z", "XR", "YR", "ZR",
        "UvSt", "Data", "FrC", "FrRt", "ColB", "ColE", "TC1", "TC2", "TC3", "TC4", "TN", "TR", "TD", "TP", "NPow", "DPow",
        "Efct", "Amb", "DstS", "Pow",   // the VFX light experiment's effector (DomeAvfxBuilder.TestLightMode)
    };

    public static ChunkNode Parse(byte[] data) => Parse(data, DefaultContainers);

    public static ChunkNode Parse(byte[] data, IReadOnlySet<string> containers)
    {
        if (data.Length < 8) throw new InvalidDataException("Too short for an AVFX file.");
        var roots = ParseRange(data, 0, data.Length, containers, "");
        if (roots.Count != 1 || roots[0].Tag != "AVFX")
            throw new InvalidDataException("An AVFX file must be exactly one AVFX chunk.");
        if (roots[0].Size != data.Length - 8)
            throw new InvalidDataException($"AVFX root size {roots[0].Size} does not match the file length {data.Length} - 8.");
        if (!roots[0].IsContainer)
            throw new InvalidDataException("The AVFX root's content is not a valid chunk sequence.");
        return roots[0];
    }

    public static string DecodeTag(ReadOnlySpan<byte> four)
    {
        Span<char> chars = stackalloc char[4];
        var n = 0;
        for (var i = 3; i >= 0; i--)
        {
            var b = four[i];
            if (b == 0) continue;
            if (b is < 0x20 or > 0x7E) throw new InvalidDataException("Chunk tag is not printable ASCII.");
            chars[n++] = (char)b;
        }
        if (n == 0) throw new InvalidDataException("Empty chunk tag.");
        return new string(chars[..n]);
    }

    private static List<ChunkNode> ParseRange(byte[] data, int start, int end, IReadOnlySet<string> containers, string parent)
    {
        var list = new List<ChunkNode>();
        var pos = start;
        while (pos < end)
        {
            if (end - pos < 8) throw new InvalidDataException($"Truncated chunk header at {pos} in {parent}.");
            var tag = DecodeTag(data.AsSpan(pos, 4));
            var size = BitConverter.ToInt32(data, pos + 4);
            if (size < 0 || size > end - pos - 8)
                throw new InvalidDataException($"Chunk {tag} at {pos} claims {size} bytes, more than its parent {parent} holds.");
            var contentStart = pos + 8;
            var padded = size + AvfxChunkWriter.Padding(size);
            if (contentStart + padded > end)
                throw new InvalidDataException($"Chunk {tag} at {pos} is missing its padding.");
            for (var i = contentStart + size; i < contentStart + padded; i++)
                if (data[i] != 0) throw new InvalidDataException($"Chunk {tag} at {pos} has non-zero padding.");

            var isContainer = containers.Contains(tag) && size >= 8 && LooksNested(data, contentStart, contentStart + size);
            var node = new ChunkNode
            {
                Tag = tag,
                Offset = pos,
                Size = size,
                Data = data.AsSpan(contentStart, size).ToArray(),
                IsContainer = isContainer,
            };
            if (isContainer) node.Children.AddRange(ParseRange(data, contentStart, contentStart + size, containers, tag));
            list.Add(node);
            pos = contentStart + padded;
        }
        return list;
    }

    private static bool LooksNested(byte[] data, int start, int end)
    {
        var pos = start;
        while (pos < end)
        {
            if (end - pos < 8) return false;
            for (var i = 0; i < 4; i++)
                if (data[pos + i] != 0 && data[pos + i] is < 0x20 or > 0x7E) return false;
            if (data[pos] == 0 && data[pos + 1] == 0 && data[pos + 2] == 0 && data[pos + 3] == 0) return false;
            var size = BitConverter.ToInt32(data, pos + 4);
            if (size < 0 || size > end - pos - 8) return false;
            pos += 8 + size + AvfxChunkWriter.Padding(size);
        }
        return pos == end;
    }
}
