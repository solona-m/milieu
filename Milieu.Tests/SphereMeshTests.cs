using System;
using System.IO;
using System.Linq;
using Milieu.GameFiles;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public class SphereMeshTests
{
    private static readonly SphereMesh Mesh = SphereMesh.Build();

    private static int Decode(byte b) => b - 128;

    [Fact] // [AC-hdri-backdrop-1]
    public void Layout_MatchesReferenceFormulas()
    {
        Assert.Equal(4753, Mesh.Vertices.Length);
        Assert.Equal(27648, Mesh.Indices.Length);

        var north = Mesh.Vertices[0];
        Assert.Equal(1.0, (double)north.Y, 3);
        Assert.Equal(-0.5, (double)north.U, 3);
        Assert.Equal(-0.5, (double)north.V, 3);

        // Vertex 97 = row 1, column 0: azimuth 90°, elevation 86.25°.
        var v97 = Mesh.Vertices[97];
        Assert.Equal(Math.Sin(86.25 * Math.PI / 180), (double)v97.Y, 3);
        Assert.Equal(Math.Cos(86.25 * Math.PI / 180), (double)v97.Z, 3);
        Assert.Equal(0.0, (double)v97.X, 3);

        var south = Mesh.Vertices[4752];
        Assert.Equal(-1.0, (double)south.Y, 3);
        Assert.Equal(0.5, (double)south.U, 3);
        Assert.Equal(0.5, (double)south.V, 3);

        foreach (var v in Mesh.Vertices)
        {
            Assert.Equal(1.0, (double)v.W);
            var dot = (double)v.X * Decode(v.NormalX) + (double)v.Y * Decode(v.NormalY) + (double)v.Z * Decode(v.NormalZ);
            Assert.True(dot < 0, "normals point inward");
        }

        Assert.Equal(new ushort[] { 0, 1, 98, 98, 97, 0 }, Mesh.Indices[..6]);
        Assert.True(Mesh.Indices.All(i => i < 4753));

        var bytes = Mesh.VertexBytes();
        Assert.Equal(4753 * 36, bytes.Length);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, bytes[16..20]);           // colour
        Assert.Equal(bytes[20..24], bytes[24..28]);                                // UV2 = UV1
        Assert.Equal(bytes[20..24], bytes[32..36]);                                // UV4 = UV1
    }

    [Fact] // [AC-hdri-backdrop-1]
    public void NormalTangent_AreBiasedBytes()
    {
        Assert.Equal(128, SphereMesh.EncodeBiased(0));
        Assert.Equal(0, SphereMesh.EncodeBiased(-1));
        Assert.Equal(255, SphereMesh.EncodeBiased(1));
        Assert.Equal(64, SphereMesh.EncodeBiased(-0.5));

        // Vertex 0 (north pole, normal straight down) as encoded bytes on disk: x 0 → 128, y −1 → 0, z 0 → 128, w 127.
        var bytes = Mesh.VertexBytes();
        Assert.Equal(new byte[] { 128, 0, 128, 127 }, bytes[8..12]);
        // Its tangent (−sin 90°, 0, cos 90°) = (−1, 0, 0) → 0, 128, 128, 127.
        Assert.Equal(new byte[] { 0, 128, 128, 127 }, bytes[12..16]);
        // South pole normal points up: y +1 → 255.
        var s = 4752 * 36;
        Assert.Equal(255, bytes[s + 9]);
    }

    [ReferenceFileFact(ReferenceFiles.Avfx)] // [AC-hdri-backdrop-1] (reference)
    public void Reference_VerticesAndIndicesMatch()
    {
        var root = AvfxChunkReader.Parse(File.ReadAllBytes(ReferenceFiles.Path(ReferenceFiles.Avfx)));
        var model = root.All("Modl").ElementAt(7);
        var vdrw = model.Need("VDrw").Data;
        var vidx = model.Need("VIdx").Data;
        var ours = Mesh.VertexBytes();
        Assert.Equal(vdrw.Length, ours.Length);
        Assert.Equal(vidx, Mesh.IndexBytes());

        for (var i = 0; i < Mesh.Vertices.Length; i++)
        {
            var o = i * 36;
            for (var k = 0; k < 4; k++)   // position
                Assert.InRange(Math.Abs((double)BitConverter.ToHalf(vdrw, o + k * 2) - (double)BitConverter.ToHalf(ours, o + k * 2)), 0, 1e-3);
            for (var k = 8; k < 20; k++)  // normal, tangent, colour bytes
                Assert.InRange(Math.Abs(vdrw[o + k] - ours[o + k]), 0, 1);
            for (var k = 20; k < 36; k += 2)  // UVs
                Assert.InRange(Math.Abs((double)BitConverter.ToHalf(vdrw, o + k) - (double)BitConverter.ToHalf(ours, o + k)), 0, 1e-3);
        }
    }
}
