using System;
using System.Numerics;

namespace Milieu.Lighting;

/// <summary>
/// Where a texel of the image appears in the world, for the dome as drawn (docs/adr/0009). Tied to
/// <see cref="GameFiles.SphereMesh"/>: vertex (c, r) has azimuth 90° + 360°·c/seg and elevation 90° − 180°·r/rings, with
/// u = −0.5 + c/seg and v = −0.5 + r/rings. The VFX scales the model UV by (±URepeat, VRepeat) (flip negates U) and the
/// game samples the texture at s = frac(u·Ux·Ku + 0.5), t = v·Vy·Kv + 0.5, with (Ku, Kv) = <see cref="GameUvScale"/>.
/// The dome's vertical scale stretches the mesh's Y; the yaw turns it about Y (as <c>VfxInterop.Place</c>). Ground
/// projection moves vertices along their own rays, so it doesn't change any direction.
/// </summary>
public static class DomeProjection
{
    /// <summary>
    /// The game's UV scale, (0.5, 1.0). Hypothesis (UNVERIFIED in game, ADR 0003's open question): model UV is measured
    /// in texture-height units, so U×2 is one wrap of a 2:1 texture and V×1 its full height, which matches the shipped
    /// repeat defaults (2, 1) tuned in game.
    /// </summary>
    public static readonly Vector2 GameUvScale = new(0.5f, 1.0f);

    private const float Eps = 1e-5f;

    /// <summary>The texture coordinates the game samples for a model UV (the forward map; s wraps, t does not).</summary>
    public static (float S, float T) TexelOf(float u, float v, DomeView view)
    {
        var ux = (view.Flip ? -view.URepeat : view.URepeat) * GameUvScale.X;
        var vy = view.VRepeat * GameUvScale.Y;
        var s = u * ux + 0.5f;
        s -= MathF.Floor(s);
        return (s, v * vy + 0.5f);
    }

    /// <summary>
    /// The world direction (unit, from the dome centre) at which texel (s,t) is drawn, or null when it has no drawn copy:
    /// horizontally the drawn range is s ∈ [0.5 − |Ux·Ku|/2, 0.5 + |Ux·Ku|/2] (the copy with the smallest |u| when several
    /// are drawn); vertically only the direct copy |v| ≤ 0.5 counts (copies outside t ∈ [0,1] depend on addressing).
    /// </summary>
    public static Vector3? WorldDirection(float s, float t, DomeView view)
    {
        var ux = (view.Flip ? -view.URepeat : view.URepeat) * GameUvScale.X;
        var vy = view.VRepeat * GameUvScale.Y;
        if (!(MathF.Abs(ux) > 0) || !(vy > 0) || !float.IsFinite(s) || !float.IsFinite(t)) return null;
        var w = s - 0.5f;
        if (MathF.Abs(w) > MathF.Abs(ux) / 2 + Eps) return null;
        var u = Math.Clamp(w / ux, -0.5f, 0.5f);
        var v = (t - 0.5f) / vy;
        if (MathF.Abs(v) > 0.5f + Eps) return null;
        v = Math.Clamp(v, -0.5f, 0.5f);
        return MeshToWorld(MeshDirection(u, v), view);
    }

    /// <summary>The unit-sphere mesh direction at model UV (u, v), per the <see cref="GameFiles.SphereMesh"/> layout.</summary>
    public static Vector3 MeshDirection(float u, float v)
    {
        var az = (90.0 + 360.0 * (u + 0.5)) * Math.PI / 180.0;
        var el = (90.0 - 180.0 * (v + 0.5)) * Math.PI / 180.0;
        return new Vector3((float)(Math.Cos(el) * Math.Cos(az)), (float)Math.Sin(el), (float)(Math.Cos(el) * Math.Sin(az)));
    }

    /// <summary>A mesh-space point's world direction: Y stretched by the vertical scale, then turned by the yaw.</summary>
    public static Vector3 MeshToWorld(Vector3 mesh, DomeView view)
    {
        var scaled = Vector3.Normalize(new Vector3(mesh.X, mesh.Y * view.VerticalScale, mesh.Z));
        return Vector3.Normalize(Vector3.Transform(scaled, Quaternion.CreateFromYawPitchRoll(view.YawRadians, 0, 0)));
    }
}
