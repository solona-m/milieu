using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Milieu.Sync;

namespace Milieu.Tests.Support;

/// <summary>Synthetic IMC files and Penumbra v1 meta strings for the synced-delivery tests.</summary>
public static class SyncFixtures
{
    /// <summary>An entry whose fields encode the part and variant, so a test can tell entries apart.</summary>
    public static ImcEntry Entry(int part, int variant, byte vfx = 0) =>
        new((byte)(10 * part + variant), (byte)part, (ushort)(0x100 + part + (variant << 10)), vfx, (byte)variant);

    /// <summary>An equipment/accessory-layout IMC (mask 0x1F, 5 parts) with variants 0..count; part 3 VFX ids as given.</summary>
    public static byte[] RingImc(int count = 3, params byte[] part3Vfx)
    {
        var rows = new List<IReadOnlyList<ImcEntry>>();
        for (var v = 0; v <= count; v++)
            rows.Add(Enumerable.Range(0, 5).Select(p =>
                Entry(p, v, p == 3 && v >= 1 && v - 1 < part3Vfx.Length ? part3Vfx[v - 1] : (byte)0)).ToList());
        return ImcFile.Build(0x1F, rows);
    }

    /// <summary>A monster-layout IMC (mask 0x1, one part) with variants 0..count.</summary>
    public static byte[] CushionImc(int count = 2, params byte[] vfx)
    {
        var rows = new List<IReadOnlyList<ImcEntry>>();
        for (var v = 0; v <= count; v++)
            rows.Add([Entry(0, v, v >= 1 && v - 1 < vfx.Length ? vfx[v - 1] : (byte)0)]);
        return ImcFile.Build(0x1, rows);
    }

    /// <summary>Penumbra's v1 meta string: base64(gzip(0x01 + "META0001" + IMC section + the other sections, empty)).</summary>
    public static string V1(IEnumerable<(ImcKey Key, ImcEntry Entry)> imc, int trailingSections = 9)
    {
        var body = new MemoryStream();
        body.WriteByte(1);
        body.Write("META0001"u8);
        var list = imc.ToList();
        body.Write(BitConverter.GetBytes(list.Count));
        foreach (var (k, e) in list)
        {
            body.Write(BitConverter.GetBytes(k.PrimaryId));
            body.WriteByte(k.Variant);
            body.WriteByte(k.ObjectType);
            body.Write(BitConverter.GetBytes(k.SecondaryId));
            body.WriteByte(k.EquipSlot);
            body.WriteByte(k.BodySlot);
            var b = new byte[ImcEntry.Size];
            e.Write(b);
            body.Write(b);
        }
        for (var i = 0; i < trailingSections; i++) body.Write(BitConverter.GetBytes(0));
        return Gzip64(body.ToArray());
    }

    public static string Gzip64(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return Convert.ToBase64String(ms.ToArray());
    }
}
