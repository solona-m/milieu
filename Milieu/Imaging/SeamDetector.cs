using System;

namespace Milieu.Imaging;

/// <summary>Seam = mean |first column − last column|; adjacent = mean |column x − column x+1|; both over sampled rows.</summary>
public readonly record struct SeamMeasure(double Seam, double Adjacent)
{
    /// <summary>The image doesn't wrap around 360° when its seam stands out from ordinary column-to-column change.</summary>
    public bool NoWrap => Seam > SeamDetector.RatioThreshold * Adjacent && Seam > SeamDetector.AbsoluteThreshold;
}

/// <summary>Detects panoramas whose left and right edges don't meet (the <c>NoWrap</c> warning). Never blocks conversion.</summary>
public static class SeamDetector
{
    public const double RatioThreshold = 4;
    public const double AbsoluteThreshold = 20;

    /// <param name="rgb">8-bit interleaved pixels with <paramref name="channels"/> channels (colour = the first 3).</param>
    public static SeamMeasure Measure(byte[] rgb, int width, int height, int channels = 3)
    {
        if (width < 2 || height < 1) return new SeamMeasure(0, 0);
        var step = Math.Max(1, height / 512);
        double seam = 0, adjacent = 0;
        long seamN = 0, adjN = 0;
        for (var y = 0; y < height; y += step)
        {
            var row = (long)y * width * channels;
            for (var c = 0; c < 3; c++)
            {
                seam += Math.Abs(rgb[row + c] - rgb[row + (long)(width - 1) * channels + c]);
                seamN++;
            }
            for (var x = 0; x < width - 1; x++)
            {
                var o = row + (long)x * channels;
                for (var c = 0; c < 3; c++) adjacent += Math.Abs(rgb[o + c] - rgb[o + channels + c]);
                adjN += 3;
            }
        }
        return new SeamMeasure(seam / seamN, adjN == 0 ? 0 : adjacent / adjN);
    }
}
