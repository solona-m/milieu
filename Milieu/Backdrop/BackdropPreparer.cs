using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Milieu.Assets;
using System.Threading;
using System.Threading.Tasks;
using Milieu.GameFiles;
using Milieu.Imaging;
using Milieu.Lighting;

namespace Milieu.Backdrop;

public interface IBackdropPreparer
{
    /// <summary>Converts (or finds in the cache) the texture for a request. Runs off the framework thread.</summary>
    Task<PreparedTexture> PrepareTextureAsync(BackdropRequest request, IProgress<float>? progress, CancellationToken ct);

    /// <summary>
    /// The light rig of an HDR image (scene lighting, docs/adr/0009): from the rig cache, or a decode (serialised with
    /// conversions). Null for a bundled id or a non-HDR file, without opening it. Throws <see cref="MilieuException"/> if
    /// the file is missing, unreadable or too large. Runs off the framework thread.
    /// </summary>
    Task<LightRig?> PrepareLightsAsync(string imagePath, CancellationToken ct);
}

/// <summary>
/// probe → limits and budget → warnings → cache → decode → (tone map) → seam check → layout, resample, band fill →
/// mips → BC7 → atomic write. Progress weights: decode 10, tone map 10, resample 10, mips 10, encode 60.
/// </summary>
/// <param name="storeRigs">Scene lighting's kill switch (Plugin.LightingAvailable): false and no conversion extracts or stores a light rig.</param>
public sealed class BackdropPreparer(CacheStore cache, IBlockEncoder encoder, IBundledBackdrops? bundled = null, ThumbStore? thumbs = null,
                                     ILogSink? log = null, bool storeRigs = true) : IBackdropPreparer
{
    private readonly LightRigStore rigs = new(cache);

    /// <summary>Test seam: float RGB → light rig (the radiance probe, then the extractor).</summary>
    internal Func<float[], int, int, CancellationToken, LightRig> RigExtractor { get; set; } =
        (rgb, w, h, ct) => LightExtractor.Extract(RadianceProbe.Build(rgb, w, h, ct));

    private readonly HashSet<string> verified = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Test seam: the pins a bundled scene is checked against (BackdropPins in the plugin).</summary>
    internal Func<string, BackdropPin?> PinLookup { get; set; } = BackdropPins.Find;
    private readonly SemaphoreSlim oneAtATime = new(1, 1);
    private int decodes, encodes;

    internal int DecodeCount => decodes;
    internal int EncodeCount => encodes;

    /// <summary>Test seam: how many rig loads missed the cache and queued for the decode slot.</summary>
    internal int RigWaits => Volatile.Read(ref rigWaits);
    private int rigWaits;

    /// <summary>Test hook: sees mip 0 RGBA before encoding.</summary>
    internal Action<byte[]>? OnMip0 { get; set; }

    /// <summary>Sees the 256x128 RGBA mip of a conversion (the user image's thumbnail).</summary>
    internal Action<byte[]>? OnThumb { get; set; }

    /// <summary>
    /// Conversions never overlap: a new one waits for the previous to finish (a decode may not be interruptible: the stb formats), so the
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
        var pin = BackdropPins.IsBundledId(id) ? PinLookup(id[BackdropPins.IdPrefix.Length..]) : null;
        if (pin == null || bundled == null)
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
            if (storeRigs) TryStoreRig(request.ImagePath, info, image.Hdr!, image.Width, image.Height, ct);
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

    /// <summary>
    /// Every HDR conversion extracts and stores the image's light rig while the float data is at hand. A failure here
    /// (extraction or storage) is logged at Debug and never fails the conversion; only cancellation passes through.
    /// </summary>
    private void TryStoreRig(string path, FileInfo info, float[] hdr, int w, int h, CancellationToken ct)
    {
        LightRig rig;
        try { rig = RigExtractor(hdr, w, h, ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log?.Debug("[Milieu] Light rig not extracted: " + LogText.Describe(e, path, cache.Root));
            return;
        }
        SaveRig(path, info, rig);
    }

    /// <summary>Stores a rig; an IO failure is logged at Debug (the rig is simply extracted again next time).</summary>
    private void SaveRig(string path, FileInfo info, LightRig rig)
    {
        try { rigs.Save(CacheStore.LightRigKey(path, info.Length, info.LastWriteTimeUtc.Ticks), rig); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Debug("[Milieu] Light rig not cached: " + LogText.Describe(e, path, cache.Root));
        }
    }

    public async Task<LightRig?> PrepareLightsAsync(string imagePath, CancellationToken ct)
    {
        if (BackdropPins.IsBundledId(imagePath) || ImageLoader.KindOf(imagePath)?.IsHdr() != true) return null;
        var file = Path.GetFileName(imagePath);
        FileInfo info;
        try
        {
            info = new FileInfo(imagePath);
            if (!info.Exists) throw new MilieuException(StatusMessage.Of(MessageKey.FileMissing, ("file", file)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new MilieuException(StatusMessage.Of(MessageKey.FileMissing, ("file", file)));
        }
        var key = CacheStore.LightRigKey(imagePath, info.Length, info.LastWriteTimeUtc.Ticks);
        if (rigs.Load(key) is { } cached) return cached;

        Interlocked.Increment(ref rigWaits);
        await oneAtATime.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (rigs.Load(key) is { } stored) return stored;   // a conversion stored it while this waited
            var probe = ImageLoader.Probe(imagePath);
            ImageLoader.CheckLimits(probe, file, probe.Width);
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref decodes);
            var image = ImageLoader.Load(imagePath, probe.Kind, ct);
            if (!image.IsHdr) return null;
            // Here an extraction failure propagates: scene lighting latches it as ReadFailed.
            var rig = RigExtractor(image.Hdr!, image.Width, image.Height, ct);
            SaveRig(imagePath, info, rig);
            return rig;
        }
        finally { oneAtATime.Release(); }
    }
}
