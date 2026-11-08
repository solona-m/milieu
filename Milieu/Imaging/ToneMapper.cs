using System;
using System.Threading;
using System.Threading.Tasks;

namespace Milieu.Imaging;

public enum ToneMapOperator { None = 0, Reinhard = 1, Aces = 2 }

/// <summary>HDR → 8-bit sRGB: linear × 2^EV, then the operator, then the sRGB transfer curve.</summary>
public static class ToneMapper
{
    public static float Operator(float x, ToneMapOperator op) => op switch
    {
        ToneMapOperator.None => Math.Clamp(x, 0f, 1f),
        ToneMapOperator.Reinhard => x / (1f + x),
        // Narkowicz 2015 ACES filmic fit.
        ToneMapOperator.Aces => Math.Clamp(x * (2.51f * x + 0.03f) / (x * (2.43f * x + 0.59f) + 0.14f), 0f, 1f),
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    public static float SrgbEncode(float linear)
    {
        linear = Math.Clamp(linear, 0f, 1f);
        return linear <= 0.0031308f ? 12.92f * linear : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
    }

    public static byte ToByte(float encoded) => (byte)Math.Clamp((int)MathF.Round(encoded * 255f), 0, 255);

    /// <summary>Maps one linear value; NaN and negatives become 0, +Inf the operator's maximum.</summary>
    public static byte Map(float linear, float exposureScale, ToneMapOperator op)
    {
        if (float.IsNaN(linear) || linear <= 0) return 0;
        if (float.IsPositiveInfinity(linear)) return 255;   // every operator saturates to 1
        // Beyond 1e6 every operator is already at 1; capping keeps ACES's x² from overflowing to NaN.
        var x = Math.Min(linear * exposureScale, 1e6f);
        if (float.IsNaN(x)) return 0;
        return ToByte(SrgbEncode(Operator(x, op)));
    }

    /// <summary>Float RGB to 8-bit RGB, in parallel row bands.</summary>
    public static byte[] ToSrgb8(float[] rgb, int width, int height, float ev, ToneMapOperator op, CancellationToken ct = default)
    {
        if (rgb.Length < (long)width * height * 3) throw new ArgumentException("Pixel buffer too small.", nameof(rgb));
        var scale = MathF.Pow(2f, float.IsFinite(ev) ? ev : 0f);
        var output = new byte[(long)width * height * 3];
        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Workers }, y =>
        {
            var start = (long)y * width * 3;
            for (long i = start; i < start + width * 3; i++) output[i] = Map(rgb[i], scale, op);
        });
        return output;
    }

    /// <summary>Half the cores, so conversion never starves the game's own threads.</summary>
    public static int Workers => Math.Max(1, Environment.ProcessorCount / 2);
}
