using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;

namespace Milieu.Imaging;

/// <summary>Encodes one RGBA mip to raw BC7 blocks. A seam so the pipeline can be tested with a fake.</summary>
public interface IBlockEncoder
{
    Task<byte[]> EncodeMipAsync(byte[] rgba, int width, int height, IProgress<float>? progress, CancellationToken ct);
}

/// <summary>
/// BC7 via managed BCnEncoder.Net: fast quality, half the cores. Each mip is encoded separately with a cancellation
/// check between mips. Inside a mip, BCnEncoder's EncodeToRawBytesAsync observes the token itself (it checks per block
/// batch; <c>Bc7Tests.CancelInsideMip0_ReturnsPromptly</c> holds it to that), so no strip fallback is needed.
/// </summary>
public sealed class Bc7 : IBlockEncoder
{
    public async Task<byte[]> EncodeMipAsync(byte[] rgba, int width, int height, IProgress<float>? progress, CancellationToken ct)
    {
        if (rgba.Length != (long)width * height * 4) throw new ArgumentException("RGBA buffer size mismatch.", nameof(rgba));
        var encoder = new BcEncoder(CompressionFormat.Bc7);
        encoder.OutputOptions.Quality = CompressionQuality.Fast;
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.Options.IsParallel = true;
        encoder.Options.TaskCount = ToneMapper.Workers;
        if (progress != null)
            encoder.Options.Progress = new Progress<ProgressElement>(p => progress.Report((float)p.Percentage));
        return await encoder.EncodeToRawBytesAsync(rgba, width, height, PixelFormat.Rgba32, 0, ct).ConfigureAwait(false);
    }

    /// <summary>Encodes every mip in order. <paramref name="progress"/> gets 0..1 over the whole chain, weighted by pixels.</summary>
    public static async Task<List<byte[]>> EncodeChainAsync(IBlockEncoder encoder, IReadOnlyList<byte[]> mips,
        IReadOnlyList<(int Width, int Height)> sizes, IProgress<float>? progress, CancellationToken ct)
    {
        double total = 0;
        foreach (var (w, h) in sizes) total += (double)w * h;
        double done = 0;
        var output = new List<byte[]>(mips.Count);
        for (var i = 0; i < mips.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (w, h) = sizes[i];
            var before = done;
            var weight = w * (double)h;
            var inner = progress == null ? null : new SyncProgress(f => progress.Report((float)((before + weight * Math.Clamp(f, 0, 1)) / total)));
            output.Add(await encoder.EncodeMipAsync(mips[i], w, h, inner, ct).ConfigureAwait(false));
            done += weight;
            progress?.Report((float)(done / total));
        }
        return output;
    }

    /// <summary>An IProgress that reports on the caller's thread (Progress&lt;T&gt; would post to a sync context).</summary>
    internal sealed class SyncProgress(Action<float> report) : IProgress<float>
    {
        public void Report(float value) => report(value);
    }
}
