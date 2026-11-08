using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Milieu.Assets;

/// <summary>Why a fetch gave up.</summary>
public enum FetchFailure
{
    None,
    /// <summary>Every source answered with a non-success status.</summary>
    Http,
    /// <summary>The body was far smaller than expected (classically a Git LFS pointer).</summary>
    TooSmall,
    /// <summary>A transport error, a stall, or a checksum that never matched.</summary>
    Transport,
    /// <summary>The verified file could not be moved into place (destination locked or not writable). Not retried.</summary>
    Io,
}

public readonly record struct FetchResult(bool Ok, FetchFailure Failure, int StatusCode, long Bytes, string Detail)
{
    public static readonly FetchResult Success = new(true, FetchFailure.None, 0, 0, "");
}

/// <summary>
/// Downloads one pinned file resiliently: several sources, retries with backoff, HTTP range resume across sessions
/// (a cancelled or interrupted fetch keeps its .tmp), a per-read stall timeout, and a SHA-256 check before the file is
/// promoted. Adapted from Proteus's ResilientDownloader (AGPL-3.0-or-later, same author) with three changes:
/// logging is strings only and sanitised (no exception objects, no local paths), the backoff delay is injectable
/// (tests), and a failed final move is reported at once as <see cref="FetchFailure.Io"/> instead of being retried.
/// </summary>
public sealed class ResilientDownloader : IDisposable
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);

    private readonly ILogSink log;
    private readonly HttpClient http;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly string? sensitiveDir;

    /// <param name="handler">Test seam; null in the plugin.</param>
    /// <param name="delay">Backoff delay; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    /// <param name="sensitiveDir">A directory (the config dir) scrubbed from every log line.</param>
    public ResilientDownloader(ILogSink log, HttpMessageHandler? handler = null,
                               Func<TimeSpan, CancellationToken, Task>? delay = null, string? sensitiveDir = null)
    {
        this.log = log;
        this.delay = delay ?? Task.Delay;
        this.sensitiveDir = sensitiveDir;
        // One client per downloader, disposed with it: a static shared client's pool timers would keep the plugin's
        // collectible load context alive after unload. Infinite timeout; ReadTimeout is the stall detector.
        http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Milieu-Plugin");
    }

    public void Dispose() => http.Dispose();

    private string Clean(Exception ex) => LogText.Describe(ex, sensitiveDir);

    public async Task<FetchResult> FetchAsync(string[] baseUrls, string fileName, long expectedBytes, string expectedSha,
                                              string destPath, Action<long>? onProgress, CancellationToken ct)
    {
        var tmp = destPath + ".tmp";
        var rng = new Random();
        var last = new FetchResult(false, FetchFailure.Transport, 0, 0, "no sources configured");

        async Task MaybeBackoff(int attempt, RetryConditionHeaderValue? retryAfter)
        {
            if (attempt < MaxAttempts) await Backoff(attempt, retryAfter, rng, ct).ConfigureAwait(false);
        }

        foreach (var baseUrl in baseUrls)
        {
            var url = baseUrl + fileName;
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var (haveBytes, h) = await RehashPartial(tmp, expectedBytes, ct).ConfigureAwait(false);
                using var hasher = h;
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    if (haveBytes > 0) req.Headers.Range = new RangeHeaderValue(haveBytes, null);
                    using var response = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                    var alreadyComplete = response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && haveBytes == expectedBytes;
                    if (!alreadyComplete && !response.IsSuccessStatusCode)
                    {
                        last = new FetchResult(false, FetchFailure.Http, (int)response.StatusCode, 0, $"HTTP {(int)response.StatusCode}");
                        log.Warning($"[Milieu] HTTP {(int)response.StatusCode} for {fileName} attempt {attempt}/{MaxAttempts}");
                        if (!IsRetryable(response.StatusCode)) break;
                        await MaybeBackoff(attempt, response.Headers.RetryAfter).ConfigureAwait(false);
                        continue;
                    }

                    if (!alreadyComplete)
                    {
                        if (haveBytes > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                        {
                            // The server ignored Range and sent the whole file: start over.
                            log.Information($"[Milieu] {fileName}: range ignored, restarting from zero");
                            hasher.GetHashAndReset();
                            TryDelete(tmp);
                            haveBytes = 0;
                        }
                        else if (response.StatusCode == HttpStatusCode.PartialContent)
                        {
                            var from = response.Content.Headers.ContentRange?.From;
                            if (from != haveBytes)
                            {
                                var detail = $"{fileName}: 206 starting at {from?.ToString() ?? "?"}, expected {haveBytes}";
                                log.Warning($"[Milieu] {detail}; discarding the partial");
                                hasher.GetHashAndReset();
                                TryDelete(tmp);
                                last = new FetchResult(false, FetchFailure.Transport, 0, 0, detail);
                                await MaybeBackoff(attempt, null).ConfigureAwait(false);
                                continue;
                            }
                        }
                        await AppendBody(response, tmp, haveBytes, hasher, onProgress, ct).ConfigureAwait(false);
                    }

                    var got = new FileInfo(tmp).Length;
                    if (expectedBytes > 1024 * 1024 && got < expectedBytes / 2)
                    {
                        TryDelete(tmp);
                        log.Warning($"[Milieu] {fileName}: only {got} bytes, possibly a Git LFS pointer");
                        last = new FetchResult(false, FetchFailure.TooSmall, 0, got, "short body");
                        break;
                    }

                    var actualSha = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                    if (got != expectedBytes || !string.Equals(actualSha, expectedSha, StringComparison.Ordinal))
                    {
                        TryDelete(tmp);
                        var detail = $"checksum mismatch for {fileName} ({got} bytes, sha256 {actualSha[..16]}…)";
                        log.Warning("[Milieu] " + detail);
                        last = new FetchResult(false, FetchFailure.Transport, 0, got, detail);
                        await MaybeBackoff(attempt, null).ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        File.Move(tmp, destPath, overwrite: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // The verified .tmp stays, so Try again only has to finish the move.
                        var detail = $"{fileName} could not be saved ({ex.GetType().Name})";
                        log.Warning($"[Milieu] {detail}: {Clean(ex)}");
                        return new FetchResult(false, FetchFailure.Io, 0, got, detail);
                    }
                    log.Information($"[Milieu] Fetched {fileName} ({got:N0} bytes)");
                    return FetchResult.Success;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
                {
                    if (ct.IsCancellationRequested) throw;   // the caller's cancel: keep the .tmp for resume
                    log.Warning($"[Milieu] {fileName} attempt {attempt}/{MaxAttempts}: {Clean(ex)}");
                    last = new FetchResult(false, FetchFailure.Transport, 0, 0, Clean(ex));
                    await MaybeBackoff(attempt, null).ConfigureAwait(false);
                }
            }
            log.Warning($"[Milieu] Giving up on one source for {fileName}");
        }
        return last;
    }

    private static async Task AppendBody(HttpResponseMessage response, string tmp, long haveBytes, IncrementalHash hasher,
                                         Action<long>? onProgress, CancellationToken ct)
    {
        await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(tmp, haveBytes > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
        var buf = new byte[1024 * 1024];
        var fileBytes = haveBytes;
        onProgress?.Invoke(fileBytes);
        while (true)
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(ReadTimeout);
            var read = await src.ReadAsync(buf, stall.Token).ConfigureAwait(false);
            if (read == 0) break;
            await dst.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
            hasher.AppendData(buf, 0, read);
            fileBytes += read;
            onProgress?.Invoke(fileBytes);
        }
    }

    private async Task<(long Have, IncrementalHash Hasher)> RehashPartial(string tmp, long expectedBytes, CancellationToken ct)
    {
        var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (!File.Exists(tmp)) return (0, hasher);
        try
        {
            var len = new FileInfo(tmp).Length;
            if (len == 0 || len > expectedBytes) { TryDelete(tmp); return (0, hasher); }
            await using var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.None);
            var buf = new byte[1024 * 1024];
            int read;
            while ((read = await fs.ReadAsync(buf, ct).ConfigureAwait(false)) > 0) hasher.AppendData(buf, 0, read);
            log.Information($"[Milieu] Resuming {Path.GetFileName(tmp)} at {len:N0} bytes");
            return (len, hasher);
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) throw;
            log.Warning($"[Milieu] Could not resume {Path.GetFileName(tmp)}, starting over: {Clean(ex)}");
            hasher.GetHashAndReset();
            TryDelete(tmp);
            return (0, hasher);
        }
    }

    private static bool IsRetryable(HttpStatusCode code) =>
        code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden
             or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable
             or HttpStatusCode.GatewayTimeout;

    private Task Backoff(int attempt, RetryConditionHeaderValue? retryAfter, Random rng, CancellationToken ct)
    {
        var wait = retryAfter?.Delta
                   ?? (retryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : (TimeSpan?)null)
                   ?? TimeSpan.FromSeconds(Math.Pow(2, attempt) + rng.NextDouble() * 2);
        if (wait < TimeSpan.Zero) wait = TimeSpan.FromSeconds(1);
        if (wait > TimeSpan.FromMinutes(2)) wait = TimeSpan.FromMinutes(2);
        return delay(wait, ct);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
