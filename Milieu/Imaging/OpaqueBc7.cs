using System;
using System.Threading;
using System.Threading.Tasks;

namespace Milieu.Imaging;

/// <summary>
/// The in-repo BC7 encoder for user images (docs/adr/0015): <see cref="Bc7Block"/> over a mip, block rows in parallel on at
/// most <see cref="ToneMapper.Workers"/> threads. Cancellation lands between rows and surfaces as a bare
/// <see cref="OperationCanceledException"/>; progress never goes backwards and ends at 1.
/// </summary>
public sealed class OpaqueBc7(int? workers = null) : IBlockEncoder
{
    /// <summary>Bump whenever this encoder's output bytes change (it is part of the texture cache key).</summary>
    public string CacheId => "opaque-bc7-1";

    /// <summary>Mode 6's block SSE above which mode 1 is tried (tuned by Bc7QualityTests, docs/adr/0015).</summary>
    internal int Mode1Threshold { get; init; } = DefaultMode1Threshold;

    /// <summary>How many of the best-ranked two-subset partitions mode 1 tries.</summary>
    internal int Mode1Partitions { get; init; } = DefaultMode1Partitions;

    internal const int DefaultMode1Threshold = 0;
    internal const int DefaultMode1Partitions = 2;

    /// <summary>Test seams: a block row starts / finishes (argument: the row).</summary>
    internal Action<int>? RowStarted { get; init; }
    internal Action<int>? RowFinished { get; init; }

    public async Task<byte[]> EncodeMipAsync(byte[] rgba, int width, int height, IProgress<float>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (width < 4 || height < 4 || width % 4 != 0 || height % 4 != 0)
            throw new ArgumentException($"Mip size {width}x{height} is not a multiple of 4.", nameof(width));
        if (rgba.Length != (long)width * height * 4) throw new ArgumentException("RGBA buffer size mismatch.", nameof(rgba));
        ct.ThrowIfCancellationRequested();

        var bw = width / 4;
        var rows = height / 4;
        var output = new byte[(long)bw * rows * 16];
        var reportEvery = Math.Max(1, rows / 128);
        var rowsDone = 0;
        var reportLock = new object();
        var lastReported = 0f;
        var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = workers ?? ToneMapper.Workers };
        var threshold = Mode1Threshold;
        var partitions = Mode1Partitions;

        // Task.Run gets no token: a cancelled run then fails with Parallel.For's own OperationCanceledException, never a
        // TaskCanceledException. The row body never checks the token, so nothing is wrapped in an AggregateException.
        await Task.Run(() => Parallel.For(0, rows, options, row =>
        {
            RowStarted?.Invoke(row);
            Span<byte> block = stackalloc byte[64];
            var outRow = output.AsSpan(row * bw * 16, bw * 16);
            for (var bx = 0; bx < bw; bx++)
            {
                for (var y = 0; y < 4; y++)
                    rgba.AsSpan(((row * 4 + y) * width + bx * 4) * 4, 16).CopyTo(block.Slice(y * 16, 16));
                Bc7Block.Encode(block, outRow.Slice(bx * 16, 16), Bc7Modes.All, threshold, partitions);
            }
            RowFinished?.Invoke(row);
            var done = Interlocked.Increment(ref rowsDone);
            if (progress != null && done % reportEvery == 0)
            {
                lock (reportLock)
                {
                    var f = (float)done / rows;
                    if (f > lastReported) { lastReported = f; progress.Report(f); }
                }
            }
        })).ConfigureAwait(false);

        if (progress != null)
            lock (reportLock)
                if (lastReported < 1f) { lastReported = 1f; progress.Report(1f); }
        return output;
    }
}
