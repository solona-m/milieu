using System;
using System.IO;

namespace Milieu.GameFiles;

/// <summary>One AVFX model vertex: 36 bytes on disk (VFXEditor <c>Formats/AvfxFormat/Model/AvfxVertexes.cs</c>).</summary>
internal readonly record struct DomeVertex(
    Half X, Half Y, Half Z, Half W,
    byte NormalX, byte NormalY, byte NormalZ, byte NormalW,
    byte TangentX, byte TangentY, byte TangentZ, byte TangentW,
    Half U, Half V)
{
    public const int Stride = 36;

    public void Write(BinaryWriter w)
    {
        w.Write(X); w.Write(Y); w.Write(Z); w.Write(W);
        w.Write(NormalX); w.Write(NormalY); w.Write(NormalZ); w.Write(NormalW);
        w.Write(TangentX); w.Write(TangentY); w.Write(TangentZ); w.Write(TangentW);
        w.Write((byte)255); w.Write((byte)255); w.Write((byte)255); w.Write((byte)255);
        for (var i = 0; i < 4; i++) { w.Write(U); w.Write(V); }   // UV1..UV4 identical, as in the reference
    }
}

/// <summary>
/// The dome mesh: a lat-long unit sphere seen from inside, laid out exactly as model 7 of the reference VFX
/// ("Pickles Venue" ve0002.avfx) so the projection (seam position, U direction, poles, winding) is the proven one.
/// Vertex <c>r·(segments+1)+c</c>: elevation 90° − 180°·r/rings, azimuth 90° + 360°·c/segments,
/// u = −0.5 + c/segments, v = −0.5 + r/rings; normals point inward.
/// </summary>
internal sealed class SphereMesh
{
    public required DomeVertex[] Vertices { get; init; }
    public required ushort[] Indices { get; init; }

    /// <summary>
    /// AVFX normals/tangents are unsigned bytes biased by 128 (VFXEditor reads them as <c>ReadByte() - 128</c>).
    /// Rounded, which matches every reference vertex to within 1.
    /// </summary>
    public static byte EncodeBiased(double component) =>
        (byte)Math.Clamp((int)Math.Round(component * 128.0, MidpointRounding.AwayFromZero) + 128, 0, 255);

    /// <summary>The reference stores 127 in the unused 4th byte of normals and tangents.</summary>
    public const byte UnusedW = 127;

    public static SphereMesh Build(int segments = 96, int rings = 48)
    {
        if (segments < 3 || rings < 2) throw new ArgumentOutOfRangeException(nameof(segments));
        var cols = segments + 1;
        var count = cols * (rings + 1);
        if (count > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(segments), "Too many vertices for 16-bit indices.");

        var verts = new DomeVertex[count];
        for (var r = 0; r <= rings; r++)
        {
            var el = (90.0 - 180.0 * r / rings) * Math.PI / 180.0;
            for (var c = 0; c <= segments; c++)
            {
                var az = (90.0 + 360.0 * c / segments) * Math.PI / 180.0;
                var px = Math.Cos(el) * Math.Cos(az);
                var py = Math.Sin(el);
                var pz = Math.Cos(el) * Math.Sin(az);
                // Snap float noise at the poles and axes so +0/-0 behave like the reference.
                px = Clean(px); py = Clean(py); pz = Clean(pz);
                var tx = Clean(-Math.Sin(az));
                var tz = Clean(Math.Cos(az));
                verts[r * cols + c] = new DomeVertex(
                    (Half)px, (Half)py, (Half)pz, (Half)1.0,
                    EncodeBiased(-px), EncodeBiased(-py), EncodeBiased(-pz), UnusedW,
                    EncodeBiased(tx), EncodeBiased(0), EncodeBiased(tz), UnusedW,
                    (Half)(-0.5 + (double)c / segments), (Half)(-0.5 + (double)r / rings));
            }
        }

        var idx = new ushort[segments * rings * 6];
        var k = 0;
        for (var r = 0; r < rings; r++)
        for (var c = 0; c < segments; c++)
        {
            var a = r * cols + c;
            idx[k++] = (ushort)a; idx[k++] = (ushort)(a + 1); idx[k++] = (ushort)(a + cols + 1);
            idx[k++] = (ushort)(a + cols + 1); idx[k++] = (ushort)(a + cols); idx[k++] = (ushort)a;
        }
        return new SphereMesh { Vertices = verts, Indices = idx };
    }

    private static double Clean(double v) => Math.Abs(v) < 1e-9 ? 0.0 : v;

    public byte[] VertexBytes()
    {
        using var ms = new MemoryStream(Vertices.Length * DomeVertex.Stride);
        using var w = new BinaryWriter(ms);
        foreach (var v in Vertices) v.Write(w);
        w.Flush();
        return ms.ToArray();
    }

    public byte[] IndexBytes()
    {
        var bytes = new byte[Indices.Length * 2];
        Buffer.BlockCopy(Indices, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
