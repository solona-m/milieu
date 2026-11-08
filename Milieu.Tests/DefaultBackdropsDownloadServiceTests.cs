using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
    private static readonly (string Name, string DisplayName, long Bytes, string Sha256)[] Pins = DefaultBackdropsDownloadService.Backdrops;

    public void Dispose() => dir.Dispose();

    /// <summary>A fake fetch that writes a file of the pinned size unless <paramref name="fail"/> says otherwise.</summary>
    private DefaultBackdropsDownloadService.Fetch Recording(Func<string, FetchResult?>? fail = null, Func<string, CancellationToken, Task>? gate = null) =>
        async (urls, name, bytes, sha, dest, _, ct) =>
        {
            calls.Enqueue((urls, name, bytes, sha));
            if (gate != null) await gate(name, ct);
            ct.ThrowIfCancellationRequested();
            if (fail?.Invoke(name) is { } f) return f;
            await using (var fs = File.Create(dest)) fs.SetLength(bytes);
            return FetchResult.Success;
        };

    private DefaultBackdropsDownloadService Service(DefaultBackdropsDownloadService.Fetch f) => new(log, dir.Path, f);

    private static void WaitIdle(DefaultBackdropsDownloadService s) =>
        Assert.True(SpinWait.SpinUntil(() => !s.IsRunActive, TimeSpan.FromSeconds(10)));

    private static FetchResult Http(int code) => new(false, FetchFailure.Http, code, 0, $"HTTP {code}");

    [Fact] // [AC-hdri-backdrop-11]
    public void Pinned_list_matches_the_committed_folder()
    {
        Assert.Equal("backdrops-v1", DefaultBackdropsDownloadService.BackdropsTag);
        var folder = RepoPaths.DefaultBackdrops();
        var committed = Directory.GetFiles(folder, "*.jpeg").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(Pins.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray(), committed);
        foreach (var p in Pins)
        {
            var path = Path.Combine(folder, p.Name);
            Assert.Equal(p.Bytes, new FileInfo(path).Length);
            using var fs = File.OpenRead(path);
            Assert.Equal(p.Sha256, Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant());
        }
    }

    [Fact] // [AC-hdri-backdrop-11]
    public void Each_fetch_passes_the_pinned_size_and_sha()
    {
        using var s = Service(Recording());
        s.EnsureAsync();
        WaitIdle(s);
        Assert.Equal(Pins.Length, calls.Count);
        foreach (var (_, name, bytes, sha) in calls)
        {
            var pin = Pins.Single(p => DefaultBackdropsDownloadService.RemoteName(p.Name) == name);
            Assert.Equal(pin.Bytes, bytes);
            Assert.Equal(pin.Sha256, sha);
        }
        Assert.Equal(PackState.Ready, s.Status.State);
        Assert.Equal(Pins.Length, s.Available().Count);
    }

    [Fact] // [AC-hdri-backdrop-11]
    public void Fetch_uses_BaseUrls_of_the_pinned_tag()
    {
        using var s = Service(Recording());
        s.EnsureAsync();
        WaitIdle(s);
        Assert.All(calls, c => Assert.Equal(MilieuAssets.BaseUrls("backdrops-v1"), c.Urls));
    }

    [Fact] // [AC-hdri-backdrop-11]
    public void Files_at_their_pinned_size_are_not_fetched_again()
    {
        using (var s = Service(Recording())) { s.EnsureAsync(); WaitIdle(s); }
        while (calls.TryDequeue(out _)) { }
        using var again = Service(Recording());
        again.EnsureAsync();
        WaitIdle(again);
        Assert.Empty(calls);
        Assert.Equal(PackState.Ready, again.Status.State);
    }

    [Fact] // [EDGE-hdri-backdrop-44]
    public void Http_404_or_410_skips_that_file_and_continues()
    {
        using var s = Service(Recording(n => n == "galaxy.jpeg" ? Http(404) : n == "lodge.jpeg" ? Http(410) : null));
        s.EnsureAsync();
        WaitIdle(s);
        Assert.Equal(Pins.Length, calls.Count);
        Assert.Equal(PackState.Failed, s.Status.State);
        Assert.False(s.Status.Unpublished);
        Assert.Equal(Pins.Length - 2, s.Available().Count);
    }

    [Fact] // [EDGE-hdri-backdrop-44]
    public void Every_file_404_ends_Failed_Unpublished_and_retry_rearms()
    {
        var publish = false;
        using var s = Service(Recording(_ => publish ? null : Http(404)));
        s.EnsureAsync();
        WaitIdle(s);
        Assert.Equal(PackState.Failed, s.Status.State);
        Assert.True(s.Status.Unpublished);
        var vm = BackdropListVm(s);
        Assert.Equal("Backdrops aren't published yet.", vm.StatusLine);
        Assert.False(vm.ShowCreditsLink);
        publish = true;
        s.EnsureAsync(retryFailed: true);
        WaitIdle(s);
        Assert.Equal(PackState.Ready, s.Status.State);
        Assert.True(BackdropListVm(s).ShowCreditsLink);
    }

    private static Gui.BackdropListViewModel BackdropListVm(DefaultBackdropsDownloadService s) => Gui.BackdropListViewModel.For(s);

    [Fact] // [EDGE-hdri-backdrop-45]
    public void Transport_failure_stops_the_run_and_Try_again_fetches_only_the_missing()
    {
        var broken = true;
        using var s = Service(Recording(n => broken && n == "livingroom.jpeg" ? new FetchResult(false, FetchFailure.Transport, 0, 0, "reset") : null));
        s.EnsureAsync();
        WaitIdle(s);
        Assert.Equal(PackState.Failed, s.Status.State);
        Assert.Equal(3, calls.Count);                         // forest, galaxy, livingroom (stopped there)
        while (calls.TryDequeue(out _)) { }
        broken = false;
        s.EnsureAsync(retryFailed: true);
        WaitIdle(s);
        Assert.Equal(Pins.Length - 2, calls.Count);           // only the ones still missing
        Assert.Equal(PackState.Ready, s.Status.State);
    }

    [Fact] // [EDGE-hdri-backdrop-45]
    public void Other_Http_status_stops_the_run()
    {
        using var s = Service(Recording(n => n == "forest.jpeg" ? Http(500) : null));
        s.EnsureAsync();
        WaitIdle(s);
        Assert.Single(calls);
        Assert.Equal(PackState.Failed, s.Status.State);
        Assert.Equal("Couldn't download the backdrops.", BackdropListVm(s).StatusLine);
    }

    [Fact] // [EDGE-hdri-backdrop-45]
    public void Unexpected_exception_fails_and_retry_rearms()
    {
        var boom = true;
        using var s = Service(async (u, n, b, sha, dest, p, ct) =>
        {
            if (boom) throw new InvalidOperationException("boom");
            return await Recording()(u, n, b, sha, dest, p, ct);
        });
        s.EnsureAsync();
        WaitIdle(s);
        Assert.Equal(PackState.Failed, s.Status.State);
        boom = false;
        s.EnsureAsync(retryFailed: true);
        WaitIdle(s);
        Assert.Equal(PackState.Ready, s.Status.State);
    }

    [Fact] // [EDGE-hdri-backdrop-46]
    public void Dispose_mid_run_cancels_without_failing_and_waits_bounded()
    {
        var sawCancel = false;
        var s = Service(Recording(gate: async (_, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { sawCancel = true; throw; }
        }));
        s.EnsureAsync();
        Assert.True(SpinWait.SpinUntil(() => !calls.IsEmpty, 5000));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        s.Dispose();
        Assert.True(sw.Elapsed < DefaultBackdropsDownloadService.DisposeWait + TimeSpan.FromSeconds(1));
        Assert.True(SpinWait.SpinUntil(() => !s.IsRunActive, 5000));
        Assert.True(sawCancel);
        Assert.NotEqual(PackState.Failed, s.Status.State);
    }

    [Fact] // [EDGE-hdri-backdrop-46]
    public void Exception_after_Dispose_is_not_a_failure()
    {
        var s = Service(Recording(gate: async (_, ct) => { await Task.Delay(50, CancellationToken.None); throw new IOException("late"); }));
        s.EnsureAsync();
        Assert.True(SpinWait.SpinUntil(() => !calls.IsEmpty, 5000));
        s.Dispose();
        Assert.True(SpinWait.SpinUntil(() => !s.IsRunActive, 5000));
        Assert.Equal(PackState.Idle, s.Status.State);
    }

    [Fact] // [EDGE-hdri-backdrop-47]
    public void Wrong_size_file_is_refetched_and_not_listed()
    {
        Directory.CreateDirectory(Path.Combine(dir.Path, "DefaultBackdrops"));
        File.WriteAllBytes(Path.Combine(dir.Path, "DefaultBackdrops", "forest.jpeg"), [1, 2, 3]);
        using var s = Service(Recording());
        Assert.DoesNotContain(s.Available(), b => b.Name == "forest.jpeg");
        Assert.False(s.IsDownloaded("forest.jpeg"));
        s.EnsureAsync();
        WaitIdle(s);
        Assert.Contains(calls, c => c.Name == "forest.jpeg");
        Assert.True(s.IsDownloaded("forest.jpeg"));
    }

    [Fact] // [EDGE-hdri-backdrop-47]
    public void Available_never_lists_tmp()
    {
        Directory.CreateDirectory(Path.Combine(dir.Path, "DefaultBackdrops"));
        File.WriteAllBytes(Path.Combine(dir.Path, "DefaultBackdrops", "forest.jpeg.tmp"), new byte[10]);
        using var s = Service(Recording());
        Assert.Empty(s.Available());
    }

    [Fact] // [EDGE-hdri-backdrop-47]
    public void Available_lists_only_pinned_names()
    {
        var d = Path.Combine(dir.Path, "DefaultBackdrops");
        Directory.CreateDirectory(d);
        File.WriteAllBytes(Path.Combine(d, "extra.jpeg"), new byte[10]);
        using var s = Service(Recording());
        s.EnsureAsync();
        WaitIdle(s);
        Assert.Equal(Pins.Select(p => p.Name), s.Available().Select(b => b.Name));
        Assert.Equal("Living Room 2", s.Available().Single(b => b.Name == "livingroom2.jpeg").DisplayName);
    }

    [Fact] // [EDGE-hdri-backdrop-48]
    public void Constructing_fetches_nothing_until_EnsureAsync()
    {
        using var s = Service(Recording());
        Thread.Sleep(50);
        Assert.Empty(calls);
        Assert.Equal(PackState.Idle, s.Status.State);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "DefaultBackdrops")));
    }

    [Fact] // [EDGE-hdri-backdrop-49]
    public void Deleted_file_alone_is_fetched_again()
    {
        using (var s = Service(Recording())) { s.EnsureAsync(); WaitIdle(s); }
        File.Delete(Path.Combine(dir.Path, "DefaultBackdrops", "sunset.jpeg"));
        while (calls.TryDequeue(out _)) { }
        using var again = Service(Recording());
        again.EnsureAsync();
        WaitIdle(again);
        Assert.Equal("sunset.jpeg", Assert.Single(calls).Name);
    }

    [Fact] // [EDGE-hdri-backdrop-53]
    public void EnsureAsync_twice_runs_once_and_retry_only_rearms_a_failed_idle_run()
    {
        var release = new TaskCompletionSource();
        using var s = Service(Recording(gate: (_, _) => release.Task));
        s.EnsureAsync();
        s.EnsureAsync();
        s.EnsureAsync(retryFailed: true);          // ignored while running
        release.SetResult();
        WaitIdle(s);
        Assert.Equal(Pins.Length, calls.Count);
        s.EnsureAsync(retryFailed: true);          // Ready, not Failed: does not re-arm
        Thread.Sleep(50);
        Assert.Equal(Pins.Length, calls.Count);
    }

    [Fact] // [EDGE-hdri-backdrop-31]
    public void Logs_NameAssetOnly()
    {
        using var s = Service(Recording(n => new FetchResult(false, FetchFailure.Transport, 0, 0, "reset")));
        s.EnsureAsync();
        WaitIdle(s);
        Assert.NotEmpty(log.Lines);
        Assert.All(log.Lines, l => Assert.DoesNotContain(dir.Path, l.Text, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(log.Lines, l => l.Text.Contains("forest.jpeg"));
    }
}
