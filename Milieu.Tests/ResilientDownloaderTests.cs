using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Milieu.Assets;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public sealed class ResilientDownloaderTests : IDisposable
{
    private readonly TempDir dir = new();
    private readonly FakeLog log = new();
    private readonly List<TimeSpan> delays = [];
    private static readonly byte[] Body = Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray();
    private static readonly string Sha = Convert.ToHexString(SHA256.HashData(Body)).ToLowerInvariant();

    public void Dispose() => dir.Dispose();

    private Task NoDelay(TimeSpan t, CancellationToken ct) { lock (delays) delays.Add(t); return Task.CompletedTask; }

    /// <summary>Serves <see cref="Body"/> through a programmable responder.</summary>
    private sealed class Handler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public readonly List<RangeHeaderValue?> Ranges = [];
        private int calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Ranges.Add(request.Headers.Range);
            return respond(request, ++calls);
        }
    }

    private static HttpResponseMessage Full(byte[]? body = null) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body ?? Body) };

    private static HttpResponseMessage Partial(long from, byte[]? body = null)
    {
        var data = (body ?? Body).Skip((int)from).ToArray();
        var r = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data) };
        r.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, Body.Length - 1, Body.Length);
        return r;
    }

    private ResilientDownloader Downloader(Handler h) => new(log, h, NoDelay, dir.Path);

    private Task<FetchResult> Fetch(ResilientDownloader d, string dest, CancellationToken ct = default) =>
        d.FetchAsync(["https://x/"], "a.jpeg", Body.Length, Sha, dest, null, ct);

    /// <summary>Streams half the body, then blocks until cancelled.</summary>
    private sealed class HalfThenHang(byte[] data) : Stream
    {
        private int pos;
        public volatile bool Hanging;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (pos < data.Length / 2)
            {
                var n = Math.Min(buffer.Length, data.Length / 2 - pos);
                data.AsMemory(pos, n).CopyTo(buffer);
                pos += n;
                return n;
            }
            Hanging = true;
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => data.Length; public override long Position { get => pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    [Fact] // [EDGE-hdri-backdrop-46]
    public async Task Cancelled_fetch_keeps_tmp_and_next_fetch_resumes_with_Range()
    {
        var dest = dir.File("a.jpeg");
        using var cts = new CancellationTokenSource();
        var body = new HalfThenHang(Body);
        var h1 = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }));
        using (var d1 = Downloader(h1))
        {
            var t = Fetch(d1, dest, cts.Token);
            Assert.True(SpinWait.SpinUntil(() => body.Hanging, 5000));   // half delivered, the read now stalls
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t);
        }
        Assert.Equal(Body.Length / 2, new FileInfo(dest + ".tmp").Length);
        Assert.False(File.Exists(dest));

        var h2 = new Handler((req, _) => Task.FromResult(Partial(req.Headers.Range!.Ranges.Single().From!.Value)));
        using var d2 = Downloader(h2);
        var r = await Fetch(d2, dest);
        Assert.True(r.Ok);
        Assert.Equal(Body.Length / 2, h2.Ranges.Single()!.Ranges.Single().From);
        Assert.Equal(Body, File.ReadAllBytes(dest));
        Assert.False(File.Exists(dest + ".tmp"));
    }

    [Fact] // [EDGE-hdri-backdrop-47]
    public async Task Integrity_failures_delete_tmp()
    {
        var dest = dir.File("a.jpeg");
        // SHA mismatch at the right length: tmp deleted, file failed.
        var bad = (byte[])Body.Clone(); bad[10] ^= 0xFF;
        using (var d = Downloader(new Handler((_, _) => Task.FromResult(Full(bad)))))
        {
            var r = await Fetch(d, dest);
            Assert.False(r.Ok);
            Assert.Equal(FetchFailure.Transport, r.Failure);
            Assert.False(File.Exists(dest + ".tmp"));
            Assert.False(File.Exists(dest));
        }
        // A tmp longer than expected is discarded before asking.
        File.WriteAllBytes(dest + ".tmp", new byte[Body.Length + 10]);
        var h = new Handler((_, _) => Task.FromResult(Full()));
        using (var d = Downloader(h))
        {
            Assert.True((await Fetch(d, dest)).Ok);
            Assert.Null(h.Ranges[0]);
        }
        File.Delete(dest);
        // A 206 that starts at the wrong offset deletes the tmp.
        File.WriteAllBytes(dest + ".tmp", Body[..1000]);
        var wrong = new Handler((_, n) => Task.FromResult(n == 1 ? Partial(500) : Full()));
        using (var d = Downloader(wrong))
        {
            Assert.True((await Fetch(d, dest)).Ok);   // second attempt starts over and succeeds
            Assert.NotNull(wrong.Ranges[0]);
            Assert.Null(wrong.Ranges[1]);
        }
        File.Delete(dest);
        // A 200 answer to a Range request restarts from zero and succeeds.
        File.WriteAllBytes(dest + ".tmp", Body[..1000]);
        var ignored = new Handler((_, _) => Task.FromResult(Full()));
        using (var d = Downloader(ignored))
        {
            Assert.True((await Fetch(d, dest)).Ok);
            Assert.Equal(1000, ignored.Ranges[0]!.Ranges.Single().From);
            Assert.Equal(Body, File.ReadAllBytes(dest));
        }
    }

    [Fact] // [EDGE-hdri-backdrop-57]
    public async Task Locked_destination_fails_file_with_sanitised_log()
    {
        var dest = dir.File("a.jpeg");
        File.WriteAllBytes(dest, [0]);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using (File.Open(dest, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var d = Downloader(new Handler((_, _) => Task.FromResult(Full()))))
        {
            var r = await Fetch(d, dest);
            Assert.False(r.Ok);
            Assert.Equal(FetchFailure.Io, r.Failure);
        }
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Empty(delays);                                  // a failed move is not retried
        Assert.True(File.Exists(dest + ".tmp"));               // the verified tmp waits for Try again
        Assert.All(log.Lines, l => Assert.DoesNotContain(dir.Path, l.Text, StringComparison.OrdinalIgnoreCase));
        Assert.All(log.Lines, l => Assert.DoesNotContain("   at ", l.Text));
        Assert.Contains(log.Lines, l => l.Text.Contains("a.jpeg"));

        // After the lock is released, Try again succeeds (re-verifying the kept tmp).
        using var d2 = Downloader(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable))));
        Assert.True((await Fetch(d2, dest)).Ok);
        Assert.Equal(Body, File.ReadAllBytes(dest));
    }

    [Fact] // [EDGE-hdri-backdrop-31]
    public async Task NeverLogsExceptionObjects()
    {
        foreach (var m in typeof(ILogSink).GetMethods())
            Assert.All(m.GetParameters(), p => Assert.False(typeof(Exception).IsAssignableFrom(p.ParameterType), $"{m.Name} takes an exception"));

        var dest = Path.Combine(dir.Path, "sub", "missing", "a.jpeg");   // directory doesn't exist → IOException on write
        using var d = Downloader(new Handler((_, _) => Task.FromResult(Full())));
        var r = await Fetch(d, dest);
        Assert.False(r.Ok);
        Assert.Contains(log.Lines, l => l.Text.Contains("IOException") || l.Text.Contains("DirectoryNotFoundException"));
        Assert.All(log.Lines, l => Assert.DoesNotContain(dir.Path, l.Text, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, delays.Count);   // retried with (injected) backoff
    }

    [Fact] // [AC-plugin-release-4] [EDGE-plugin-release-11]
    public async Task Mirror_404_falls_back_to_GitHub()
    {
        var urls = new List<string>();
        var h = new Handler((req, _) =>
        {
            lock (urls) urls.Add(req.RequestUri!.ToString());
            return Task.FromResult(req.RequestUri!.Host == "dl.solona.info" ? new HttpResponseMessage(HttpStatusCode.NotFound) : Full());
        });
        using var d = Downloader(h);
        var dest = Path.Combine(dir.Path, "moon.atex");
        var r = await d.FetchAsync(MilieuAssets.BaseUrls("backdrops-v2"), "moon.atex", Body.Length, Sha, dest, null, default);
        Assert.True(r.Ok, r.Detail);
        Assert.Equal(new[] { "https://dl.solona.info/milieu/backdrops-v2/moon.atex", "https://github.com/solona-m/milieu/releases/download/backdrops-v2/moon.atex" }, urls);
        Assert.Empty(delays);                                   // a 404 needs no backoff
        Assert.Equal(Body, File.ReadAllBytes(dest));
    }

    [Fact] // [EDGE-plugin-release-11]
    public async Task Unreachable_mirror_falls_back_to_GitHub()
    {
        var hosts = new List<string>();
        var h = new Handler((req, _) =>
        {
            lock (hosts) hosts.Add(req.RequestUri!.Host);
            if (req.RequestUri!.Host == "dl.solona.info") throw new HttpRequestException("unreachable");
            return Task.FromResult(Full());
        });
        using var d = Downloader(h);
        var dest = Path.Combine(dir.Path, "moon.atex");
        var r = await d.FetchAsync(MilieuAssets.BaseUrls("backdrops-v2"), "moon.atex", Body.Length, Sha, dest, null, default);
        Assert.True(r.Ok, r.Detail);
        Assert.Equal(5, hosts.Count(x => x == "dl.solona.info"));
        Assert.Equal("github.com", hosts[^1]);
        Assert.Equal(4, delays.Count);                          // backoff between the mirror's attempts (injected)
    }
}
