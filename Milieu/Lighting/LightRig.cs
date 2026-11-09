using System.Numerics;

namespace Milieu.Lighting;

/// <summary>A light source found in the image: its direction as image coordinates (s,t in [0,1]) and its energy per channel.</summary>
public sealed record RigLight(float S, float T, Vector3 Energy);

/// <summary>
/// What the panorama's light is made of, in image space (independent of how the dome is drawn): an optional key (sun or
/// brightest region), four median-cut regions of everything else ranked by radiance (energy per solid angle), and the
/// total energy. Energies are Σ L·dω per channel.
/// <para>
/// An estimated rig (an 8-bit image, docs/adr/0010; <see cref="EstimatorVersion"/> above 0) also has up to three
/// <see cref="Lamps"/>. Its key and lamps carry only a colour of luminance 1 times a relative weight (lamp Ω / key Ω):
/// the brightness is applied at plan time (<see cref="LightRigPlanner.Resolve"/>), and its <see cref="TotalEnergy"/> is
/// the regions' sum.
/// </para>
/// </summary>
public sealed record LightRig(int Version, RigLight? Key, RigLight[] Regions, Vector3 TotalEnergy, RigLight[]? Lamps = null,
                              int EstimatorVersion = 0)
{
    public const int RegionCount = 4;
    public const int MaxLamps = 3;

    /// <summary>The estimated lamps (never null; empty for an HDR rig).</summary>
    public RigLight[] Lamps { get; init; } = Lamps ?? [];

    /// <summary>Estimated from an 8-bit image rather than extracted from HDR data.</summary>
    public bool IsEstimated => EstimatorVersion > 0;

    public static float Luminance(Vector3 rgb) => 0.2126f * rgb.X + 0.7152f * rgb.Y + 0.0722f * rgb.Z;
}
