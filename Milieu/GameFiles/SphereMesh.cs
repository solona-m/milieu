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
/// The dome meshes. <see cref="Build"/> is a lat-long unit sphere seen from inside, laid out exactly as model 7 of the
/// reference VFX ("Pickles Venue" ve0002.avfx) so the projection (seam position, U direction, poles, winding) is the proven
/// one. Vertex <c>r·(segments+1)+c</c>: elevation 90° − 180°·r/rings, azimuth 90° + 360°·c/segments,
/// u = −0.5 + c/segments, v = −0.5 + r/rings; normals point inward. The half-dome meshes (docs/adr/0015) keep those column,
/// U, normal, tangent and winding formulas: <see cref="BuildProjected"/> has its own ring elevations (v follows them) and
/// <see cref="BuildOpenBottom"/> is the reference's upper half, a byte prefix of it.
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

    /// <summary>The reference mesh (model 7's layout).</summary>
    public static SphereMesh Build(int segments = 96, int rings = 48) => BuildReference(segments, rings, null, rings);

    /// <summary>
    /// The pre-0015 ground projection, kept byte-for-byte for the kill switch (<c>Plugin.HalfDomeAvailable</c>): every vertex
    /// below the floor plane y = −floorY (0 &lt; floorY &lt; 1, unit-radius space) is pulled in along its own ray onto that
    /// plane (position = d · min(1, floorY / −d.y)). UVs, normals, tangents and indices are the reference's.
    /// </summary>
    public static SphereMesh BuildLegacyFloor(float floorY) => BuildReference(96, 48, floorY, 48);

    /// <summary>The reference sphere's upper half (rows 0..24, elevation 90°..0°): its buffers are prefixes of <see cref="Build"/>'s.</summary>
    public static SphereMesh BuildOpenBottom() => BuildReference(96, 48, null, 24);

    /// <summary>The mesh for <paramref name="kind"/>; the floor kinds need <paramref name="floorY"/>, the others ignore it.</summary>
    public static SphereMesh For(DomeMeshKind kind, float? floorY) => kind switch
    {
        DomeMeshKind.Sphere => Build(),
        DomeMeshKind.LegacyFloor => BuildLegacyFloor(floorY ?? throw new ArgumentOutOfRangeException(nameof(floorY), "The legacy floor needs floorY.")),
        DomeMeshKind.ImageFloor => BuildProjected(floorY ?? throw new ArgumentOutOfRangeException(nameof(floorY), "The image floor needs floorY.")),
        DomeMeshKind.OpenBottom => BuildOpenBottom(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), $"Unknown dome mesh kind {(int)kind}."),
    };

    /// <summary>
    /// The image-floor half dome (docs/adr/0015): rows at <see cref="DomeShape.RingElevations"/>, each vertex cast from the
    /// capture point onto the hemisphere-on-the-floor (<see cref="DomeShape.CastImageFloor"/>); v = −0.5 + (90° − el)/180°.
    /// Throws <see cref="InvalidDataException"/> when the rows or their Half v are not strictly monotonic.
    /// </summary>
    public static SphereMesh BuildProjected(float floorY, int segments = 96)
    {
        if (segments < 3) throw new ArgumentOutOfRangeException(nameof(segments));
        var els = DomeShape.RingElevations(floorY);
        var cols = segments + 1;
        var count = cols * els.Length;
        if (count > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(segments), "Too many vertices for 16-bit indices.");
        var vs = new Half[els.Length];
        for (var r = 0; r < els.Length; r++)
        {
            vs[r] = (Half)(-0.5 + (Math.PI / 2 - els[r]) / Math.PI);
            if (r > 0 && (!(els[r] < els[r - 1]) || !(vs[r] > vs[r - 1])))
                throw new InvalidDataException($"Dome rows must be strictly monotonic (row {r}).");
        }

        var verts = new DomeVertex[count];
        for (var r = 0; r < els.Length; r++)
        {
            var el = els[r];
            for (var c = 0; c <= segments; c++)
            {
                var az = (90.0 + 360.0 * c / segments) * Math.PI / 180.0;
                var dx = Clean(Math.Cos(el) * Math.Cos(az));
                var dy = Clean(Math.Sin(el));
                var dz = Clean(Math.Cos(el) * Math.Sin(az));
                var (px, py, pz) = DomeShape.CastImageFloor(dx, dy, dz, floorY);
                verts[r * cols + c] = new DomeVertex(
                    (Half)Clean(px), (Half)Clean(py), (Half)Clean(pz), (Half)1.0,
                    EncodeBiased(-dx), EncodeBiased(-dy), EncodeBiased(-dz), UnusedW,
                    EncodeBiased(Clean(-Math.Sin(az))), EncodeBiased(0), EncodeBiased(Clean(Math.Cos(az))), UnusedW,
                    (Half)(-0.5 + (double)c / segments), vs[r]);
            }
        }
        return new SphereMesh { Vertices = verts, Indices = BandIndices(segments, els.Length - 1) };
    }

    private static ushort[] BandIndices(int segments, int bands)
    {
        var cols = segments + 1;
        var idx = new ushort[segments * bands * 6];
        var k = 0;
        for (var r = 0; r < bands; r++)
        for (var c = 0; c < segments; c++)
        {
            var a = r * cols + c;
            idx[k++] = (ushort)a; idx[k++] = (ushort)(a + 1); idx[k++] = (ushort)(a + cols + 1);
            idx[k++] = (ushort)(a + cols + 1); idx[k++] = (ushort)(a + cols); idx[k++] = (ushort)a;
        }
        return idx;
    }

    private static SphereMesh BuildReference(int segments, int rings, float? floorY, int bands)
    {
        if (segments < 3 || rings < 2) throw new ArgumentOutOfRangeException(nameof(segments));
        if (floorY is { } f && !(f > 0 && f < 1)) throw new ArgumentOutOfRangeException(nameof(floorY), "floorY must be in (0, 1).");
        var cols = segments + 1;
        var count = cols * (bands + 1);
        if (count > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(segments), "Too many vertices for 16-bit indices.");

        var verts = new DomeVertex[count];
        for (var r = 0; r <= bands; r++)
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
                // Normals stay those of the sphere direction d (inward), whatever the projection.
                double nx = px, ny = py, nz = pz;
                if (floorY is { } floor && py < 0)
                {
                    var s = Math.Min(1.0, floor / -py);
                    px *= s; py *= s; pz *= s;
                }
                var tx = Clean(-Math.Sin(az));
                var tz = Clean(Math.Cos(az));
                verts[r * cols + c] = new DomeVertex(
                    (Half)px, (Half)py, (Half)pz, (Half)1.0,
                    EncodeBiased(-nx), EncodeBiased(-ny), EncodeBiased(-nz), UnusedW,
                    EncodeBiased(tx), EncodeBiased(0), EncodeBiased(tz), UnusedW,
                    (Half)(-0.5 + (double)c / segments), (Half)(-0.5 + (double)r / rings));
            }
        }

        return new SphereMesh { Vertices = verts, Indices = BandIndices(segments, bands) };
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
