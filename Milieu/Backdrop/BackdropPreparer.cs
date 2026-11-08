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
    private readonly SemaphoreSlim oneAtATime = new(1, 1);
    private int decodes, encodes;

    internal int DecodeCount => decodes;
    internal int EncodeCount => encodes;

    /// <summary>Test hook: sees mip 0 RGBA before encoding.</summary>
    internal Action<byte[]>? OnMip0 { get; set; }

    /// <summary>Sees the 256x128 RGBA mip of a conversion (the user image's thumbnail).</summary>
    internal Action<byte[]>? OnThumb { get; set; }

    /// <summary>
    /// Conversions never overlap: a new one waits for the previous to finish (its decode cannot be interrupted), so the
    /// memory budget holds even if Show is pressed repeatedly.
    /// </summary>
    public async Task<PreparedTexture> PrepareTextureAsync(BackdropRequest request, IProgress<float>? progress, CancellationToken ct)
    {
        await oneAtATime.WaitAsync(ct).ConfigureAwait(false);
        try { return await PrepareCoreAsync(request, progress, ct).ConfigureAwait(false); }
        finally { oneAtATime.Release(); }
    }

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

        Interlocked.Increment(ref encodes);
        var convertProgress = progress == null ? null : new Bc7.SyncProgress(f => progress.Report(0.20f + 0.80f * f));
        var (srcW, srcH) = (image.Width, image.Height);
        image = null!;
        var blocks = await TextureConversion.EncodeAsync(rgb, srcW, srcH, outW, outH, encoder, convertProgress, ct, OnMip0, OnThumb)
                                            .ConfigureAwait(false);
        rgb = null!;

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
