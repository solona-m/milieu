namespace Milieu.Lighting;

/// <summary>Estimates scene lighting from an 8-bit image (docs/adr/0010).</summary>
public static class LdrLightEstimator
{
    /// <summary>A source pixel is clipped when min(R,G,B) is at least this.</summary>
    public const int ClipLevel = 245;
}
