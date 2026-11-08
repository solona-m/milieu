using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Milieu.Assets;
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
public sealed class BackdropPreparer(CacheStore cache, IBlockEncoder encoder, IBundledBackdrops? bundled = null, ThumbStore? thumbs = null) : IBackdropPreparer
{
    private readonly HashSet<string> verified = new(StringComparer.OrdinalIgnoreCase);
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
        if (BackdropPins.IsBundledId(request.ImagePath)) return PrepareBundled(request.ImagePath, progress);
        await oneAtATime.WaitAsync(ct).ConfigureAwait(false);
        try { return await PrepareCoreAsync(request, progress, ct).ConfigureAwait(false); }
        finally { oneAtATime.Release(); }
    }

    /// <summary>
    /// A bundled scene: its downloaded, pre-converted .atex is checked (pinned size, exact header, SHA-256 once per
    /// session per file version) and registered as-is. Max width, exposure and tone map don't apply.
    /// </summary>
    private PreparedTexture PrepareBundled(string id, IProgress<float>? progress)
    {
        if (!BackdropPins.TryParseId(id, out var pin) || bundled == null)
            throw new MilieuException(StatusMessage.Of(MessageKey.FileMissing, ("file", id)));
        var name = pin.DisplayName;
        if (!bundled.IsDownloaded(pin.Stem)) throw new MilieuException(StatusMessage.Of(MessageKey.BundledNotDownloaded, ("name", name)));
        var path = bundled.PathOf(pin.Stem);
        try
        {
            var info = new FileInfo(path);
            var versionKey = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            bool ok;
            lock (verified) ok = verified.Contains(versionKey);
            if (!ok)
            {
                using var fs = File.OpenRead(path);
                var expected = AtexWriter.ExpectedHeader(BackdropPins.Width, BackdropPins.Height);
                var head = new byte[expected.Length];
                fs.ReadExactly(head);
                fs.Position = 0;
                ok = info.Length == pin.Bytes && head.AsSpan().SequenceEqual(expected)
                     && Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant() == pin.Sha256;
                if (ok) lock (verified) verified.Add(versionKey);
            }
            if (!ok)
            {
                bundled.MarkCorrupt(pin.Stem);
                throw new MilieuException(StatusMessage.Of(MessageKey.BundledCorrupt, ("name", name)));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            bundled.MarkCorrupt(pin.Stem);
            throw new MilieuException(StatusMessage.Of(MessageKey.BundledCorrupt, ("name", name)));
        }
        progress?.Report(1f);
        var key = pin.Sha256[..16];
        return new PreparedTexture(key, path, CacheStore.TextureGamePath(key), BackdropPins.Width, BackdropPins.Height, ImageKind.Jpeg, [],
                                   Preconverted: true);
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
        Action<byte[]> onThumb = t =>
        {
            OnThumb?.Invoke(t);
            try { thumbs?.Save(request.ImagePath, t); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* a missing preview is not an error */ }
        };
        var blocks = await TextureConversion.EncodeAsync(rgb, srcW, srcH, outW, outH, encoder, convertProgress, ct, OnMip0, onThumb)
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
