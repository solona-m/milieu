using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Milieu.Sync;

/// <summary>
/// Penumbra's meta-manipulation strings, both base64(gzip(version byte + payload)):
/// v0 = JSON (what Milieu writes, accepted by every Penumbra API 5), v1 = "META0001" + binary sections (what
/// GetPlayerMetaManipulations returns; Milieu reads only its IMC section, the first one).
/// </summary>
public static class PenumbraMeta
{
    private static readonly byte[] V1Prefix = "META0001"u8.ToArray();
    private const int ImcRecordSize = 8 + ImcEntry.Size;
    private const int MaxDecompressed = 16 << 20;

    /// <summary>The IMC overrides in a v1 string; anything else (empty, v0, v2, bad base64, truncated) → empty. Never throws.</summary>
    public static Dictionary<ImcKey, ImcEntry> ReadImcOverrides(string? manipBase64) => TryReadImcOverrides(manipBase64, out var map) ? map : map;

    /// <summary>
    /// As <see cref="ReadImcOverrides"/>, with a status: false when a non-empty string couldn't be read as v1 (the caller
    /// logs it at Debug and falls back to vanilla). An empty string is readable: no overrides.
    /// </summary>
    public static bool TryReadImcOverrides(string? manipBase64, out Dictionary<ImcKey, ImcEntry> result)
    {
        result = new Dictionary<ImcKey, ImcEntry>();
        if (string.IsNullOrEmpty(manipBase64)) return true;
        try
        {
            var raw = Convert.FromBase64String(manipBase64);
            var data = Gunzip(raw);
            if (data == null || data.Length < 1 + V1Prefix.Length + 4 || data[0] != 1) return false;
            var span = data.AsSpan(1);
            if (!span.StartsWith(V1Prefix)) return false;
            span = span[V1Prefix.Length..];
            var count = BinaryPrimitives.ReadInt32LittleEndian(span);
            span = span[4..];
            if (count < 0 || (long)count * ImcRecordSize > span.Length) return false;
            for (var i = 0; i < count; i++)
            {
                var rec = span.Slice(i * ImcRecordSize, ImcRecordSize);
                var key = new ImcKey(BinaryPrimitives.ReadUInt16LittleEndian(rec), rec[2], rec[3],
                                     BinaryPrimitives.ReadUInt16LittleEndian(rec[4..]), rec[6], rec[7]);
                result[key] = ImcEntry.Read(rec[8..]);
            }
            return true;
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or ArgumentException or IOException)
        {
            result = new Dictionary<ImcKey, ImcEntry>();
            return false;
        }
    }

    /// <summary>
    /// The v0 manipulation string for the target's variants: one IMC manipulation each, keys in the order Penumbra's own
    /// serializer writes them (Entry, then ObjectType, PrimaryId, Variant, then the slot fields for the object type).
    /// </summary>
    public static string EncodeImcV0(SyncTarget target, IReadOnlyList<(byte Variant, ImcEntry Entry)> entries)
    {
        using var json = new MemoryStream();
        using (var w = new Utf8JsonWriter(json))
        {
            w.WriteStartArray();
            foreach (var (variant, e) in entries)
            {
                w.WriteStartObject();
                w.WriteString("Type", "Imc");
                w.WriteStartObject("Manipulation");
                w.WriteStartObject("Entry");
                w.WriteNumber("MaterialId", e.MaterialId);
                w.WriteNumber("DecalId", e.DecalId);
                w.WriteNumber("VfxId", e.VfxId);
                w.WriteNumber("MaterialAnimationId", e.MaterialAnimationId);
                w.WriteNumber("AttributeMask", e.AttributeMask);
                w.WriteNumber("SoundId", e.SoundId);
                w.WriteEndObject();
                w.WriteString("ObjectType", target.ObjectType);
                w.WriteNumber("PrimaryId", target.PrimaryId);
                w.WriteNumber("Variant", variant);
                if (target.EquipSlot != null) w.WriteString("EquipSlot", target.EquipSlot);
                else
                {
                    w.WriteNumber("SecondaryId", target.SecondaryId);
                    w.WriteString("BodySlot", target.BodySlot);
                }
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        using var gz = new MemoryStream();
        using (var z = new GZipStream(gz, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.WriteByte(0);
            json.Position = 0;
            json.CopyTo(z);
        }
        return Convert.ToBase64String(gz.ToArray());
    }

    /// <summary>Decompresses a gzip buffer, refusing anything larger than 16 MiB; null when it isn't gzip.</summary>
    internal static byte[]? Gunzip(byte[] raw)
    {
        if (raw.Length < 2 || raw[0] != 0x1F || raw[1] != 0x8B) return null;
        using var z = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress);
        using var o = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = z.Read(buf, 0, buf.Length)) > 0)
        {
            o.Write(buf, 0, n);
            if (o.Length > MaxDecompressed) return null;
        }
        return o.ToArray();
    }

    /// <summary>For tests: the UTF-8 JSON inside a v0 string.</summary>
    internal static string DecodeV0Json(string manipBase64)
    {
        var data = Gunzip(Convert.FromBase64String(manipBase64)) ?? throw new InvalidDataException("not gzip");
        if (data[0] != 0) throw new InvalidDataException("not v0");
        return Encoding.UTF8.GetString(data, 1, data.Length - 1);
    }
}
