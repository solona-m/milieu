using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Milieu.Assets;

public enum EntryState { NotDownloaded, Queued, Downloading, Downloaded, Failed, Unpublished }

/// <summary>One bundled scene's download state. <see cref="Bytes"/> is the bytes on disk while downloading.</summary>
public sealed record EntryStatus(EntryState State, long Bytes, string Detail)
{
    public float Fraction(long total) => total <= 0 ? 0 : Math.Clamp((float)Bytes / total, 0, 1);
}

/// <summary>What the preparer needs to show a bundled scene.</summary>
public interface IBundledBackdrops
{
    bool IsDownloaded(string stem);
    string PathOf(string stem);

    /// <summary>The file failed its checks: delete it so it is downloaded again.</summary>
    void MarkCorrupt(string stem);
}

/// <summary>
/// Milieu's bundled scenes: pre-converted .atex release assets (<see cref="BackdropPins"/>), never in the plugin zip.
/// Downloaded one scene at a time, only when asked for (a tile click or "Download all"), into
/// <c>&lt;config&gt;/DefaultBackdrops</c>. Present = at the pinned size. A 404/410 marks that scene unpublished and moves
/// on; any other failure fails that scene and drains the queue. A cancelled download keeps its .tmp for range resume.
/// </summary>
public sealed class DefaultBackdropsDownloadService : IBundledBackdrops, Gui.IGridDownloads, IDisposable
{
    internal delegate Task<FetchResult> Fetch(string[] baseUrls, string fileName, long expectedBytes, string expectedSha,
                                              string destPath, Action<long>? onProgress, CancellationToken ct);

    public static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(2);

    private readonly ILogSink log;
    private readonly ResilientDownloader? downloader;
    private readonly Fetch fetch;
    private readonly string dir;
    private readonly CancellationTokenSource cts = new();
    private readonly object gate = new();
    private readonly Queue<string> queue = new();
    private readonly Dictionary<string, EntryStatus> status = new(StringComparer.OrdinalIgnoreCase);
    private Task? worker;
    private long version;

    /// <summary>
    /// A scene finished downloading. Fires on the download worker thread and only signals: consumers read state on
    /// the framework tick or the draw (no game or Penumbra call may happen here).
    /// </summary>
    public event Action<string>? EntryCompleted;

    public string Directory => dir;

    public DefaultBackdropsDownloadService(ILogSink log, string configDir) : this(log, configDir, null) { }

    internal DefaultBackdropsDownloadService(ILogSink log, string configDir, Fetch? fetch)
    {
        this.log = log;
        if (fetch == null) downloader = new ResilientDownloader(log, sensitiveDir: configDir);
        this.fetch = fetch ?? downloader!.FetchAsync;
        dir = Path.Combine(configDir, "DefaultBackdrops");
    }

    /// <summary>Bumped on every change so the UI can re-read cheaply.</summary>
    public long Version => Interlocked.Read(ref version);

    public string PathOf(string stem) => Path.Combine(dir, (BackdropPins.Find(stem)?.Stem ?? stem) + ".atex");

    public bool IsDownloaded(string stem) => BackdropPins.Find(stem) is { } pin && AtPinnedSize(PathOf(pin.Stem), pin.Bytes);

    public EntryStatus StatusOf(string stem)
    {
        lock (gate)
        {
            if (status.TryGetValue(stem, out var s) && s.State is not (EntryState.Downloaded or EntryState.NotDownloaded)) return s;
        }
        return IsDownloaded(stem) ? new EntryStatus(EntryState.Downloaded, 0, "") : new EntryStatus(EntryState.NotDownloaded, 0, "");
    }

    public bool AnyMissing => BackdropPins.All.Any(p => !IsDownloaded(p.Stem));

    /// <summary>The release isn't there yet: some scene answered 404/410 and none is on disk.</summary>
    public bool PackUnpublished => BackdropPins.All.Any(p => StatusOf(p.Stem).State == EntryState.Unpublished)
                                   && BackdropPins.All.All(p => !IsDownloaded(p.Stem));

    internal bool IsRunActive { get { lock (gate) return worker is { IsCompleted: false }; } }

    /// <summary>Queues one scene (no-op when it is downloaded, queued or downloading).</summary>
    public void Request(string stem)
    {
        if (BackdropPins.Find(stem) is not { } pin) return;
        lock (gate)
        {
            if (cts.IsCancellationRequested) return;
            var current = status.TryGetValue(pin.Stem, out var s) ? s.State : EntryState.NotDownloaded;
            if (current is EntryState.Queued or EntryState.Downloading) return;
            if (IsDownloaded(pin.Stem)) return;
            status[pin.Stem] = new EntryStatus(EntryState.Queued, 0, "");
            queue.Enqueue(pin.Stem);
            Bump();
            if (worker is not { IsCompleted: false }) worker = Task.Run(Run);
        }
    }

    /// <summary>Queues every missing scene in table order.</summary>
    public void RequestAll()
    {
        foreach (var pin in BackdropPins.All) Request(pin.Stem);
    }

    private async Task Run()
    {
        while (true)
        {
            string stem;
            lock (gate)
            {
                if (queue.Count == 0 || cts.IsCancellationRequested) return;
                stem = queue.Dequeue();
                status[stem] = new EntryStatus(EntryState.Downloading, 0, "");
                Bump();
            }
            var pin = BackdropPins.Find(stem)!;
            var completed = false;
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                var r = await fetch(MilieuAssets.BaseUrls(BackdropPins.Tag), BackdropPins.RemoteName(pin), pin.Bytes, pin.Sha256,
                                    PathOf(stem), bytes => SetBytes(stem, bytes), cts.Token).ConfigureAwait(false);
                if (r.Ok)
                {
                    Set(stem, EntryState.Downloaded, "");
                    completed = true;
                }
                else if (r.Failure == FetchFailure.Http && r.StatusCode is 404 or 410)
                {
                    Set(stem, EntryState.Unpublished, r.Detail);
                    log.Warning($"[Milieu] Backdrop {pin.FileName} isn't published yet ({r.Detail})");
                }
                else
                {
                    Set(stem, EntryState.Failed, r.Detail);
                    log.Warning($"[Milieu] Backdrop {pin.FileName} unavailable ({r.Detail})");
                    Drain();
                }
            }
            catch (Exception) when (cts.IsCancellationRequested)
            {
                Set(stem, EntryState.NotDownloaded, "");   // unloading: not a failure; the .tmp stays for resume
                return;
            }
            catch (Exception ex)
            {
                Set(stem, EntryState.Failed, ex.GetType().Name);
                log.Warning($"[Milieu] Downloading {pin.FileName} failed: " + LogText.Describe(ex, dir));
                Drain();
            }
            if (completed)
            {
                try { EntryCompleted?.Invoke(stem); }
                catch (Exception e) { log.Warning("[Milieu] EntryCompleted handler failed: " + LogText.Describe(e, dir)); }
            }
        }
    }

    /// <summary>After a real failure the rest of the queue goes back to "not downloaded" until asked again.</summary>
    private void Drain()
    {
        lock (gate)
        {
            while (queue.Count > 0) status[queue.Dequeue()] = new EntryStatus(EntryState.NotDownloaded, 0, "");
            Bump();
        }
    }

    private void Set(string stem, EntryState state, string detail)
    {
        lock (gate) { status[stem] = new EntryStatus(state, 0, detail); Bump(); }
    }

    private void SetBytes(string stem, long bytes)
    {
        lock (gate) { status[stem] = new EntryStatus(EntryState.Downloading, bytes, ""); Bump(); }
    }

    private void Bump() => Interlocked.Increment(ref version);

    public void MarkCorrupt(string stem)
    {
        try { if (File.Exists(PathOf(stem))) File.Delete(PathOf(stem)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Warning("[Milieu] Could not delete a damaged backdrop: " + LogText.Describe(e, dir)); }
        Set(stem, EntryState.NotDownloaded, "");
    }

    /// <summary>
    /// Deletes leftovers at the top of the download folder: v1 JPEGs (and their partials) and .atex files that are not
    /// pinned. Pinned .atex files and anything else are kept. Runs on a background task at startup.
    /// </summary>
    public int CleanupStale()
    {
        if (!System.IO.Directory.Exists(dir)) return 0;
        var deleted = 0;
        foreach (var f in System.IO.Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(f);
            var stale = name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".jpeg.tmp", StringComparison.OrdinalIgnoreCase)
                        || ((name.EndsWith(".atex", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".atex.tmp", StringComparison.OrdinalIgnoreCase))
                            && BackdropPins.Find(name.Split('.')[0]) == null);
            if (!stale) continue;
            try { File.Delete(f); deleted++; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* retried next start */ }
        }
        return deleted;
    }

    private static bool AtPinnedSize(string path, long bytes)
    {
        try { return File.Exists(path) && new FileInfo(path).Length == bytes; }
        catch (Exception) { return false; }
    }

    /// <summary>Cancels the queue, waits up to 2 s for the worker, then releases the HTTP client.</summary>
    public void Dispose()
    {
        Task? t;
        lock (gate)
        {
            cts.Cancel();
            t = worker;
        }
        try { t?.Wait(DisposeWait); }
        catch (AggregateException) { }
        downloader?.Dispose();
    }
}
