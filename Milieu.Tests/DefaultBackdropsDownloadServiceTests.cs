using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Milieu.Assets;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public sealed class DefaultBackdropsDownloadServiceTests : IDisposable
{
    private readonly TempDir dir = new();
    private readonly FakeLog log = new();
    private readonly ConcurrentQueue<(string[] Urls, string Name, long Bytes, string Sha)> calls = new();

    public void Dispose() => dir.Dispose();

    private string Folder => Path.Combine(dir.Path, "DefaultBackdrops");

    /// <summary>A fake fetch that writes a file of the pinned size unless <paramref name="fail"/> says otherwise.</summary>
    private DefaultBackdropsDownloadService.Fetch Recording(Func<string, FetchResult?>? fail = null, Func<string, Action<long>?, CancellationToken, Task>? gate = null) =>
        async (urls, name, bytes, sha, dest, progress, ct) =>
        {
            calls.Enqueue((urls, name, bytes, sha));
            if (gate != null) await gate(name, progress, ct);
            ct.ThrowIfCancellationRequested();
            if (fail?.Invoke(name) is { } f) return f;
            await using (var fs = File.Create(dest)) fs.SetLength(bytes);
            return FetchResult.Success;
        };

    private DefaultBackdropsDownloadService Service(DefaultBackdropsDownloadService.Fetch f) => new(log, dir.Path, f);

    private static void WaitIdle(DefaultBackdropsDownloadService s) =>
        Assert.True(Eventually.Holds(() => !s.IsRunActive));

    private static FetchResult Http(int code) => new(false, FetchFailure.Http, code, 0, $"HTTP {code}");

    [Fact] // [AC-hdri-backdrop-11] [EDGE-hdri-backdrop-48]
    public void Nothing_downloads_until_an_entry_is_requested()
    {
        using var s = Service(Recording());
        Thread.Sleep(50);
        Assert.Empty(calls);
        Assert.False(Directory.Exists(Folder));
        Assert.All(BackdropPins.All, p => Assert.Equal(EntryState.NotDownloaded, s.StatusOf(p.Stem).State));
    }

    [Fact] // [AC-hdri-backdrop-11]
    public void Request_fetches_only_that_entry_with_pinned_size_and_sha()
    {
        using var s = Service(Recording());
        s.Request("moon");
        WaitIdle(s);
        var c = Assert.Single(calls);
        var pin = BackdropPins.Find("moon")!;
        Assert.Equal("moon.atex", c.Name);
        Assert.Equal(pin.Bytes, c.Bytes);
        Assert.Equal(pin.Sha256, c.Sha);
        Assert.EndsWith("/backdrops-v2/", c.Urls[^1]);
        Assert.True(s.IsDownloaded("moon"));
        Assert.Equal(EntryState.Downloaded, s.StatusOf("moon").State);
        Assert.False(s.IsDownloaded("forest"));
    }

    [Fact] // [AC-hdri-backdrop-11]
    public void RequestAll_downloads_missing_one_at_a_time_in_table_order()
    {
        Directory.CreateDirectory(Folder);
        using (var fs = File.Create(Path.Combine(Folder, "lodge.atex"))) fs.SetLength(BackdropPins.Find("lodge")!.Bytes);
        var active = 0;
        var maxActive = 0;
        using var s = Service(Recording(gate: async (_, _, _) =>
        {
            var now = Interlocked.Increment(ref active);
            maxActive = Math.Max(maxActive, now);
            await Task.Delay(5);
            Interlocked.Decrement(ref active);
        }));
        s.RequestAll();
        WaitIdle(s);
        Assert.Equal(BackdropPins.All.Where(p => p.Stem != "lodge").Select(p => p.FileName), calls.Select(c => c.Name));
        Assert.Equal(1, maxActive);
        Assert.False(s.AnyMissing);
    }

    [Fact] // [AC-hdri-backdrop-11]
    public void Per_entry_progress_is_reported()
    {
        var release = new TaskCompletionSource();
        using var s = Service(Recording(gate: async (_, progress, _) => { progress?.Invoke(1000); await release.Task; }));
        s.Request("forest");
        Assert.True(Eventually.Holds(() => s.StatusOf("forest").Bytes == 1000));
        Assert.Equal(EntryState.Downloading, s.StatusOf("forest").State);
        s.Request("galaxy");
        Assert.Equal(EntryState.Queued, s.StatusOf("galaxy").State);
        release.SetResult();
        WaitIdle(s);
    }

    [Fact] // [EDGE-hdri-backdrop-44]
    public void Http_404_or_410_marks_entry_unpublished_and_continues()
    {
        using var s = Service(Recording(n => n == "galaxy.atex" ? Http(404) : n == "lodge.atex" ? Http(410) : null));
        s.RequestAll();
        WaitIdle(s);
        Assert.Equal(BackdropPins.Count, calls.Count);
        Assert.Equal(EntryState.Unpublished, s.StatusOf("galaxy").State);
        Assert.Equal(EntryState.Unpublished, s.StatusOf("lodge").State);
        Assert.True(s.IsDownloaded("moon"));
        Assert.False(s.PackUnpublished);   // some are downloaded
    }

    [Fact] // [EDGE-hdri-backdrop-44]
    public void Unpublished_pack_when_any_404_and_none_downloaded()
    {
        using var s = Service(Recording(_ => Http(404)));
        s.Request("forest");
        WaitIdle(s);
        Assert.True(s.PackUnpublished);
    }

    [Fact] // [EDGE-hdri-backdrop-45]
    public void Transport_failure_fails_entry_and_drains_queue_TryAgain_refetches()
    {
        var broken = true;
        var release = new TaskCompletionSource();
        using var s = Service(Recording(n => broken && n == "forest.atex" ? new FetchResult(false, FetchFailure.Transport, 0, 0, "reset") : null,
                                        gate: (n, _, _) => n == "forest.atex" ? release.Task : Task.CompletedTask));
        s.Request("forest");
        s.Request("galaxy");
        s.Request("moon");
        release.SetResult();
        WaitIdle(s);
        Assert.Equal(EntryState.Failed, s.StatusOf("forest").State);
        Assert.Equal(EntryState.NotDownloaded, s.StatusOf("galaxy").State);   // drained, not fetched
        Assert.Single(calls);
        broken = false;
        s.Request("forest");
        WaitIdle(s);
        Assert.True(s.IsDownloaded("forest"));
    }

    [Fact] // [EDGE-hdri-backdrop-46]
    public void Dispose_mid_download_cancels_queue_without_failing_and_waits_bounded()
    {
        var sawCancel = false;
        var s = Service(Recording(gate: async (_, _, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { sawCancel = true; throw; }
        }));
        s.Request("forest");
        s.Request("galaxy");
        Assert.True(Eventually.Holds(() => !calls.IsEmpty));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        s.Dispose();
        Assert.True(sw.Elapsed < DefaultBackdropsDownloadService.DisposeWait + TimeSpan.FromSeconds(1));
        Assert.True(Eventually.Holds(() => !s.IsRunActive));
        Assert.True(sawCancel);
        Assert.NotEqual(EntryState.Failed, s.StatusOf("forest").State);
        Assert.Single(calls);
    }

    [Fact] // [EDGE-hdri-backdrop-53]
    public void Duplicate_requests_fetch_once()
    {
        var release = new TaskCompletionSource();
        using var s = Service(Recording(gate: (_, _, _) => release.Task));
        s.Request("moon"); s.Request("moon"); s.Request("MOON");
        release.SetResult();
        WaitIdle(s);
        s.Request("moon");   // downloaded: no-op
        Thread.Sleep(30);
        Assert.Single(calls);
    }

    [Fact] // [EDGE-hdri-backdrop-63]
    public void CleanupStale_deletes_v1_jpegs_and_unpinned_atex_keeps_pinned_and_others()
    {
        Directory.CreateDirectory(Path.Combine(Folder, "sub"));
        foreach (var f in new[] { "forest.jpeg", "moon.jpeg", "galaxy.jpeg.tmp", "old.atex", "old.atex.tmp", "lodge.atex", "lodge.atex.tmp", "notes.txt", "sub/x.jpeg" })
            File.WriteAllBytes(Path.Combine(Folder, f), [1]);
        using var s = Service(Recording());
        Assert.Equal(5, s.CleanupStale());
        Assert.Equal(new[] { "lodge.atex", "lodge.atex.tmp", "notes.txt" }, Directory.GetFiles(Folder).Select(Path.GetFileName).OrderBy(n => n));
        Assert.True(File.Exists(Path.Combine(Folder, "sub", "x.jpeg")));
    }

    [Fact] // [EDGE-hdri-backdrop-92]
    public void EntryCompleted_OnlyBumpsVersion()
    {
        using var s = Service(Recording());
        var controller = new FakeGridController();
        string? completed = null;
        var thread = 0;
        s.EntryCompleted += stem => { completed = stem; thread = Environment.CurrentManagedThreadId; };
        var v = s.Version;
        s.Request("sunset");
        WaitIdle(s);
        Assert.Equal("sunset", completed);
        Assert.NotEqual(Environment.CurrentManagedThreadId, thread);   // the worker thread
        Assert.True(s.Version > v);
        Assert.Empty(controller.Calls);                                // nothing reached a controller
    }

    [Fact] // [EDGE-hdri-backdrop-57]
    public void Io_failure_fails_entry_and_Try_again_succeeds()
    {
        var locked = true;
        using var s = Service(Recording(n => locked && n == "forest.atex" ? new FetchResult(false, FetchFailure.Io, 0, 0, "forest.atex could not be saved") : null));
        s.Request("forest");
        WaitIdle(s);
        Assert.Equal(EntryState.Failed, s.StatusOf("forest").State);
        locked = false;
        s.Request("forest");
        WaitIdle(s);
        Assert.True(s.IsDownloaded("forest"));
    }

    [Fact] // [EDGE-hdri-backdrop-31]
    public void Logs_NameAssetOnly()
    {
        using var s = Service(Recording(_ => new FetchResult(false, FetchFailure.Transport, 0, 0, "reset")));
        s.Request("forest");
        WaitIdle(s);
        Assert.NotEmpty(log.Lines);
        Assert.All(log.Lines, l => Assert.DoesNotContain(dir.Path, l.Text, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(log.Lines, l => l.Text.Contains("forest.atex"));
    }

    [Fact] // [EDGE-hdri-backdrop-62]
    public void MarkCorrupt_deletes_file_and_resets_entry()
    {
        using var s = Service(Recording());
        s.Request("moon");
        WaitIdle(s);
        s.MarkCorrupt("moon");
        Assert.False(File.Exists(s.PathOf("moon")));
        Assert.Equal(EntryState.NotDownloaded, s.StatusOf("moon").State);
    }
}
