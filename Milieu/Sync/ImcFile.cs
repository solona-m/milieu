using System;
using System.Collections.Generic;
using System.Numerics;

namespace Milieu.Sync;

/// <summary>One 6-byte IMC entry, laid out as in the game file and Penumbra's ImcEntry.</summary>
public readonly record struct ImcEntry(byte MaterialId, byte DecalId, ushort AttributeAndSound, byte VfxId, byte MaterialAnimationId)
{
    public const int Size = 6;

    public ushort AttributeMask => (ushort)(AttributeAndSound & 0x3FF);

    public byte SoundId => (byte)(AttributeAndSound >> 10);

    public ImcEntry WithVfx(byte vfxId) => this with { VfxId = vfxId };

    public static ImcEntry Read(ReadOnlySpan<byte> b) =>
        new(b[0], b[1], (ushort)(b[2] | (b[3] << 8)), b[4], b[5]);

    public void Write(Span<byte> b)
    {
        b[0] = MaterialId;
        b[1] = DecalId;
        b[2] = (byte)AttributeAndSound;
        b[3] = (byte)(AttributeAndSound >> 8);
        b[4] = VfxId;
        b[5] = MaterialAnimationId;
    }
}

/// <summary>
/// A bounds-checked reader for the game's <c>.imc</c> files: a header (u16 variant count, u16 part mask), then the
/// default variant 0 and variants 1..count, each holding one 6-byte entry per part present in the mask. An entry is
/// addressed exactly as Penumbra's <c>ImcFile.VariantPtr</c> does: <c>variant × popcount(mask) + partIndex</c> (the raw
/// part index, not its rank among the set bits), so Milieu reads the entry Penumbra and the game read.
/// </summary>
public static class ImcFile
{
    /// <summary>The entries of variants 1..count of one part; false for anything malformed.</summary>
    public static bool TryRead(byte[]? data, int partIndex, out IReadOnlyList<ImcEntry> variants)
    {
        variants = [];
        if (data == null || data.Length < 4 || partIndex is < 0 or > 15) return false;
        var count = data[0] | (data[1] << 8);
        var mask = data[2] | (data[3] << 8);
        if (count < 1 || (mask & (1 << partIndex)) == 0) return false;
        var parts = BitOperations.PopCount((uint)mask);
        long needed = 4L + (long)ImcEntry.Size * parts * (count + 1);
        if (data.Length < needed) return false;
        var list = new List<ImcEntry>(count);
        for (var v = 1; v <= count; v++)
        {
            long offset = 4L + (long)ImcEntry.Size * ((long)v * parts + partIndex);
            if (offset + ImcEntry.Size > data.Length) return false;
            list.Add(ImcEntry.Read(data.AsSpan((int)offset, ImcEntry.Size)));
        }
        variants = list;
        return true;
    }

    /// <summary>Builds an .imc (for tests and tools): every variant holds the same entries, one per part in the mask.</summary>
    public static byte[] Build(ushort mask, IReadOnlyList<IReadOnlyList<ImcEntry>> variantsIncludingZero)
    {
        var parts = BitOperations.PopCount(mask);
        var count = variantsIncludingZero.Count - 1;
        var data = new byte[4 + ImcEntry.Size * parts * (count + 1)];
        data[0] = (byte)count; data[1] = (byte)(count >> 8);
        data[2] = (byte)mask; data[3] = (byte)(mask >> 8);
        for (var v = 0; v <= count; v++)
            for (var p = 0; p < parts; p++)
                variantsIncludingZero[v][p].Write(data.AsSpan(4 + ImcEntry.Size * (v * parts + p), ImcEntry.Size));
        return data;
    }
}
