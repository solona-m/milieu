using System;

namespace Milieu.Imaging;

/// <summary>
/// Fills the rows above and below a horizon band. Each filled row fades from the band's edge row, box-blurred
/// horizontally with wrap (radius width/64), toward that edge row's mean colour: colour = lerp(B[x], M, min(1, d/F))
/// where d is the distance from the band (0 for the row next to it). Near the poles everything is the flat mean, so no streaks converge there.
/// Shared by the plugin (runtime band images) and tools/PrepareBackdrops (Sunset).
/// </summary>
public static class BandFill
{
    /// <param name="pixels">Interleaved image with <paramref name="channels"/> channels (3 or 4), band rows already drawn.</param>
    public static void Fill(byte[] pixels, int width, int height, int channels, int bandTop, int bandHeight, int fadeRows)
    {
        if (bandHeight <= 0 || bandTop < 0 || bandTop + bandHeight > height) throw new ArgumentOutOfRangeException(nameof(bandTop));
        if (fadeRows < 1) fadeRows = 1;
        var colourChannels = Math.Min(channels, 3);

        FillSide(pixels, width, channels, colourChannels, bandTop, bandTop - 1, -1, 0, fadeRows);
        FillSide(pixels, width, channels, colourChannels, bandTop + bandHeight - 1, bandTop + bandHeight, +1, height - 1, fadeRows);
    }

    private static void FillSide(byte[] px, int width, int channels, int cc, int edgeRow, int firstRow, int step, int lastRow, int fade)
    {
        if (step < 0 ? firstRow < lastRow : firstRow > lastRow) return;
        var blurred = BlurRow(px, width, channels, cc, edgeRow, Math.Max(1, width / 64));
        var mean = new double[cc];
        for (var x = 0; x < width; x++)
            for (var c = 0; c < cc; c++) mean[c] += px[((long)edgeRow * width + x) * channels + c];
        for (var c = 0; c < cc; c++) mean[c] /= width;

        for (var y = firstRow; step < 0 ? y >= lastRow : y <= lastRow; y += step)
        {
            var d = Math.Abs(y - edgeRow) - 1;   // the row next to the band is distance 0
            var t = Math.Min(1.0, (double)d / fade);
            var row = (long)y * width * channels;
            for (var x = 0; x < width; x++)
            {
                var o = row + (long)x * channels;
                for (var c = 0; c < cc; c++)
                    px[o + c] = (byte)Math.Clamp((int)Math.Round(blurred[x * cc + c] * (1 - t) + mean[c] * t), 0, 255);
                for (var c = cc; c < channels; c++) px[o + c] = 255;
            }
        }
    }

    /// <summary>Horizontal box blur of one row, wrapping around (the panorama is periodic in x).</summary>
    internal static double[] BlurRow(byte[] px, int width, int channels, int cc, int row, int radius)
    {
        var result = new double[width * cc];
        var window = 2 * radius + 1;
        var sum = new double[cc];
        long Idx(int x) => ((long)row * width + ((x % width) + width) % width) * channels;
        for (var x = -radius; x <= radius; x++)
            for (var c = 0; c < cc; c++) sum[c] += px[Idx(x) + c];
        for (var x = 0; x < width; x++)
        {
            for (var c = 0; c < cc; c++) result[x * cc + c] = sum[c] / window;
            for (var c = 0; c < cc; c++) sum[c] += px[Idx(x + radius + 1) + c] - px[Idx(x - radius) + c];
        }
        return result;
    }
}
