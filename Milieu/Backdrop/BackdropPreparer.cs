using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Milieu.GameFiles;
using Milieu.Imaging;

namespace Milieu.Backdrop;

public interface IBackdropPreparer
{
    /// <summary>Converts (or finds in the cache) the texture for a request. Runs off the framework thread.</summary>
    Task<PreparedTexture> PrepareTextureAsync(BackdropRequest request, IProgress<float>? progress, CancellationToken ct);
}

/// <summary>
/// probe → limits and budget → warnings → cache → decode → (tone map) → seam check → layout, resample, band fill →
/// mips → BC7 → atomic write. Progress weights: decode 10, tone map 10, resample 10, mips 10, encode 60.
/// </summary>
public sealed class BackdropPreparer(CacheStore cache, IBlockEncoder encoder) : IBackdropPreparer
{
    private int decodes, encodes;

    internal int DecodeCount => decodes;
    internal int EncodeCount => encodes;

    /// <summary>Test hook: sees mip 0 RGBA before encoding.</summary>
    internal Action<byte[]>? OnMip0 { get; set; }

    public Task<PreparedTexture> PrepareTextureAsync(BackdropRequest request, IProgress<float>? progress, CancellationToken ct) =>
        PrepareCoreAsync(request, progress, ct);

    private async Task<PreparedTexture> PrepareCoreAsync(BackdropRequest request, IProgress<float>? progress, CancellationToken ct)
    {
        var file = Path.GetFileName(request.ImagePath);
        var probe = ImageLoader.Probe(request.ImagePath);
        var (outW, outH) = BackdropSizing.OutputSize(probe.Width, request.MaxWidth);
        var warnings = new List<StatusMessage>(ImageLoader.CheckLimits(probe, file, outW));
        if (EquirectLayout.ShapeOf(probe.Width, probe.Height) == PanoramaShape.Stretched)
            warnings.Add(StatusMessage.Of(MessageKey.NotTwoToOne, ("file", file)));

        var info = new FileInfo(request.ImagePath);
        var key = CacheStore.TextureKey(request, probe.Kind, info.Length, info.LastWriteTimeUtc.Ticks);
        var layout = AtexWriter.Layout(outW, outH);
        var diskPath = cache.TextureDiskPath(key);
        if (cache.TryGetTexture(key, layout.TotalLength))
        {
            progress?.Report(1f);
            return new PreparedTexture(key, diskPath, CacheStore.TextureGamePath(key), outW, outH, probe.Kind, warnings);
        }

        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref decodes);
        var image = ImageLoader.Load(request.ImagePath, probe.Kind, ct);
        progress?.Report(0.10f);

        byte[] rgb;
        if (image.IsHdr)
        {
            rgb = ToneMapper.ToSrgb8(image.Hdr!, image.Width, image.Height, request.ExposureEv, request.ToneMap, ct);
            image = new LoadedImage { Width = image.Width, Height = image.Height, Rgb = rgb };   // drop the float buffer
        }
        else rgb = image.Rgb!;
        progress?.Report(0.20f);

        if (SeamDetector.Measure(rgb, image.Width, image.Height).NoWrap)
            warnings.Add(StatusMessage.Of(MessageKey.NoWrap, ("file", file)));

        ct.ThrowIfCancellationRequested();
        var rgba = EquirectResampler.ResampleLayout(rgb, image.Width, image.Height, outW, outH, ct);
        rgb = null!;
        image = null!;
        progress?.Report(0.30f);

        var mips = MipChain.Build(rgba, outW, outH, ct);
        OnMip0?.Invoke(mips[0]);
        progress?.Report(0.40f);

        Interlocked.Increment(ref encodes);
        var encodeProgress = progress == null ? null : new Bc7.SyncProgress(f => progress.Report(0.40f + 0.60f * f));
        var blocks = await Bc7.EncodeChainAsync(encoder, mips, MipChain.Sizes(outW, outH), encodeProgress, ct).ConfigureAwait(false);
        mips = null!;

        ct.ThrowIfCancellationRequested();
        try
        {
            cache.WriteAtomic(diskPath, s => AtexWriter.Write(s, outW, outH, blocks));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new MilieuException(StatusMessage.Of(MessageKey.WriteFailed));
        }
        progress?.Report(1f);
        return new PreparedTexture(key, diskPath, CacheStore.TextureGamePath(key), outW, outH, probe.Kind, warnings);
    }
}
