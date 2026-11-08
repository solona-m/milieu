using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Milieu.GameFiles;
using Milieu.Imaging;

namespace Milieu.Backdrop;

/// <summary>
/// 8-bit RGB panorama → BC7 <c>.atex</c>: layout + resample (with band fill), mip chain, BC7 per mip, AtexWriter.
/// Pure and shared: the plugin converts user images with it and tools/PrepareBackdrops builds the pre-converted
/// bundled textures with it, so both produce the same bytes (docs/adr/0005).
/// </summary>
public static class TextureConversion
{
    public const int ThumbWidth = 256;
    public const int ThumbHeight = 128;

    /// <summary>
    /// Encodes <paramref name="rgb"/> (w×h, 3 bytes per pixel) to BC7 mips at outW×outH. Progress is 0..1 over
    /// resample (1/6), mips (1/6) and encoding (4/6). <paramref name="onMip0"/> sees the full-size RGBA and
    /// <paramref name="onThumb"/> the 256x128 RGBA level (present for every output size Milieu uses).
    /// </summary>
    public static async Task<List<byte[]>> EncodeAsync(byte[] rgb, int w, int h, int outW, int outH, IBlockEncoder encoder,
        IProgress<float>? progress, CancellationToken ct, Action<byte[]>? onMip0 = null, Action<byte[]>? onThumb = null)
    {
        ct.ThrowIfCancellationRequested();
        var rgba = EquirectResampler.ResampleLayout(rgb, w, h, outW, outH, ct);
        progress?.Report(1f / 6);
        var mips = MipChain.Build(rgba, outW, outH, ct);
        var sizes = MipChain.Sizes(outW, outH);
        onMip0?.Invoke(mips[0]);
        if (onThumb != null)
            for (var i = 0; i < sizes.Count; i++)
                if (sizes[i] == (ThumbWidth, ThumbHeight)) onThumb(mips[i]);
        progress?.Report(2f / 6);
        var encodeProgress = progress == null ? null : new Bc7.SyncProgress(f => progress.Report(2f / 6 + 4f / 6 * f));
        return await Bc7.EncodeChainAsync(encoder, mips, sizes, encodeProgress, ct).ConfigureAwait(false);
    }

    /// <summary>Encodes and writes a complete <c>.atex</c> to <paramref name="output"/>.</summary>
    public static async Task ConvertToAtexAsync(byte[] rgb, int w, int h, int outW, int outH, IBlockEncoder encoder, Stream output,
        IProgress<float>? progress = null, CancellationToken ct = default, Action<byte[]>? onMip0 = null, Action<byte[]>? onThumb = null)
    {
        var blocks = await EncodeAsync(rgb, w, h, outW, outH, encoder, progress, ct, onMip0, onThumb).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        AtexWriter.Write(output, outW, outH, blocks);
    }
}
