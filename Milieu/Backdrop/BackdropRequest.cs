using System.Collections.Generic;
using Milieu.Imaging;

namespace Milieu.Backdrop;

/// <summary>What to convert. EV and tone map only matter for HDR images.</summary>
public sealed record BackdropRequest(string ImagePath, int MaxWidth, float ExposureEv, ToneMapOperator ToneMap)
{
    public static BackdropRequest From(Configuration c) => new(c.ImagePath ?? "", c.MaxTextureWidth, c.ExposureEv, c.ToneMap);
}

/// <summary>A converted texture in the cache, plus the warnings found on the way.</summary>
/// <param name="Preconverted">A bundled scene: the downloaded .atex is used as-is (no decode, no encode).</param>
public sealed record PreparedTexture(string Key, string DiskPath, string GamePath, int Width, int Height, ImageKind Kind,
                                     IReadOnlyList<StatusMessage> Warnings, bool Preconverted = false);
