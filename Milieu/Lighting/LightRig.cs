using System.Numerics;

namespace Milieu.Lighting;

/// <summary>A light source found in the image: its direction as image coordinates (s,t in [0,1]) and its energy per channel.</summary>
public sealed record RigLight(float S, float T, Vector3 Energy);

/// <summary>
/// What the panorama's light is made of, in image space (independent of how the dome is drawn): an optional key (sun or
/// brightest region), four median-cut regions of everything else ranked by radiance (energy per solid angle), and the
/// total energy. Energies are Σ L·dω per channel.
/// </summary>
public sealed record LightRig(int Version, RigLight? Key, RigLight[] Regions, Vector3 TotalEnergy)
{
    public const int RegionCount = 4;

    public static float Luminance(Vector3 rgb) => 0.2126f * rgb.X + 0.7152f * rgb.Y + 0.0722f * rgb.Z;
}
