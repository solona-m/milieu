namespace Milieu.Imaging;

public enum PanoramaShape { Full, Band, Stretched }

/// <summary>Where the source lands in the 2:1 output: whole frame, a horizon band, or stretched.</summary>
public readonly record struct EquirectPlacement(PanoramaShape Shape, int BandTop, int BandHeight);

/// <summary>
/// Decides how a panorama maps onto the 2:1 output. Within 1% of 2:1 it fills the frame. Wider images are a horizon
/// band (vertical coverage 180° × 2h/w), centred on the equator with no stretching; the rows outside are filled by
/// <see cref="BandFill"/>. Taller images are stretched (and the user warned).
/// </summary>
public static class EquirectLayout
{
    public const double MinFull = 1.98;
    public const double MaxFull = 2.02;

    public static PanoramaShape ShapeOf(int w, int h)
    {
        var a = (double)w / h;
        return a > MaxFull ? PanoramaShape.Band : a < MinFull ? PanoramaShape.Stretched : PanoramaShape.Full;
    }

    public static EquirectPlacement Place(int srcW, int srcH, int outW, int outH)
    {
        var shape = ShapeOf(srcW, srcH);
        if (shape != PanoramaShape.Band) return new EquirectPlacement(shape, 0, outH);
        var bandH = (int)System.Math.Max(1, System.Math.Round((double)outW * srcH / srcW));
        if (bandH > outH) bandH = outH;
        return new EquirectPlacement(shape, (outH - bandH) / 2, bandH);
    }
}
