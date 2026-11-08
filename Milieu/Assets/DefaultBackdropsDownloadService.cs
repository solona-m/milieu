using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Milieu.Assets;

public enum PackState { Idle, Downloading, Ready, Failed }

/// <summary>The bundled pack's download status. <see cref="Unpublished"/>: every asset answered 404/410 (no release yet).</summary>
public sealed record PackStatus(PackState State, int Have, int Total, string Detail, bool Unpublished);

/// <summary>A bundled backdrop on disk (only listed once it is at its pinned size).</summary>
public sealed record BundledBackdrop(string Name, string DisplayName, string Path);

/// <summary>
/// Milieu's bundled backdrops: pinned release assets (tag <see cref="BackdropsTag"/>), never in the plugin zip.
/// Downloaded lazily into <c>&lt;config&gt;/DefaultBackdrops</c> the first time the Backdrops list is drawn, never at
/// plugin load. Follows Proteus's DefaultMatcapsDownloadService: present = at the pinned size; a 404/410 skips that
/// file; any other failure stops the run until Try again; a cancelled download keeps its .tmp for range resume.
/// </summary>
public sealed class DefaultBackdropsDownloadService : IDisposable
{
    /// <summary>
    /// Immutable by contract: bump it whenever <see cref="Backdrops"/> changes and never re-upload assets under an
    /// existing tag (a mirror would keep serving the old bytes). upload-backdrops.yml publishes it.
    /// </summary>
    internal const string BackdropsTag = "backdrops-v1";

    public const string CreditsUrl = "https://github.com/" + MilieuAssets.Repo + "/blob/" + BackdropsTag + "/Milieu/DefaultBackdrops/CREDITS.md";

    /// <summary>
    /// Must equal the committed <c>Milieu/DefaultBackdrops</c> folder byte for byte
    /// (<c>DefaultBackdropsDownloadServiceTests.Pinned_list_matches_the_committed_folder</c>).
    /// </summary>
    internal static readonly (string Name, string DisplayName, long Bytes, string Sha256)[] Backdrops =
    [
        ("forest.jpeg", "Forest", 11438345L, "4f50adab0cacdcb44464c4db66edf4a83b5cc6de76ce237b0837ac0e650d4017"),
        ("galaxy.jpeg", "Galaxy", 5164434L, "543a9c982849726862a721e6a6f64cc2361afa35e8e160087e4c67916e407757"),
        ("livingroom.jpeg", "Living Room", 6014821L, "63010dea376a722b331256c0a93fdb4266bde072ee228f4c69e87a821641840a"),
        ("livingroom2.jpeg", "Living Room 2", 5701238L, "bf80174da981527a7f250f6d4b2e552d79deb3b587671028896363b969c7bee6"),
        ("lodge.jpeg", "Lodge", 14927085L, "0a2bde2818f55b08b422f508494880a273398543b133ecd72254bc5107245fec"),
        ("sunset.jpeg", "Sunset", 2175541L, "19583850892488e1d60f0220393140e7e4a43d8233b870dc8b8ff51702789dee"),
        ("warehouse.jpeg", "Warehouse", 7416775L, "7fb67402575e408b303730c21a606e43d4a3d0c95673a93c15da6c8130f7886a"),
    ];

    public static long TotalBytes => Backdrops.Sum(b => b.Bytes);

    /// <summary>The asset name to ask for: GitHub rewrites spaces, and upload-backdrops.yml uploads with this substitution.</summary>
    internal static string RemoteName(string localName) => localName.Replace(' ', '.');

    internal delegate Task<FetchResult> Fetch(string[] baseUrls, string fileName, long expectedBytes, string expectedSha,
                                              string destPath, Action<long>? onProgress, CancellationToken ct);

    public static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(2);

    private readonly ILogSink log;
    private readonly ResilientDownloader? downloader;
    private readonly Fetch fetch;
    private readonly string dir;
    private readonly CancellationTokenSource cts = new();
    private readonly object gate = new();
    private PackStatus status;
    private bool running;
    private bool started;
    private Task? runTask;
    private long version;

    public string Directory => dir;

    public DefaultBackdropsDownloadService(ILogSink log, string configDir) : this(log, configDir, null) { }

    internal DefaultBackdropsDownloadService(ILogSink log, string configDir, Fetch? fetch)
    {
        this.log = log;
        if (fetch == null) downloader = new ResilientDownloader(log, sensitiveDir: configDir);
        this.fetch = fetch ?? downloader!.FetchAsync;
        dir = Path.Combine(configDir, "DefaultBackdrops");
        status = new PackStatus(PackState.Idle, 0, Backdrops.Length, "", false);
    }

    public PackStatus Status { get { lock (gate) return status; } }

    /// <summary>Bumped on every change, so the UI can re-read cheaply.</summary>
    public long Version => Interlocked.Read(ref version);

    internal bool IsRunActive { get { lock (gate) return running; } }

    public string PathOf(string name) => Path.Combine(dir, name);

    public bool IsDownloaded(string name)
    {
        var pin = Backdrops.FirstOrDefault(b => b.Name == name);
        return pin.Name != null && AtPinnedSize(PathOf(name), pin.Bytes);
    }

    public IReadOnlyList<BundledBackdrop> Available() =>
        Backdrops.Where(b => AtPinnedSize(PathOf(b.Name), b.Bytes))
                 .Select(b => new BundledBackdrop(b.Name, b.DisplayName, PathOf(b.Name)))
                 .ToList();

    /// <summary>
    /// Starts the download once. <paramref name="retryFailed"/> re-arms a failed, finished run (Try again);
    /// a call while a run is active does nothing.
    /// </summary>
    public void EnsureAsync(bool retryFailed = false)
    {
        lock (gate)
        {
            if (running) return;
            var rearm = retryFailed && status.State == PackState.Failed;
            if (started && !rearm) return;
            started = true;
            running = true;
            status = status with { State = PackState.Downloading, Detail = "", Have = CountHave(), Unpublished = false };
            Interlocked.Increment(ref version);
            runTask = Task.Run(Run);
        }
    }

    private async Task Run()
    {
        var end = PackState.Ready;
        var detail = "";
        var absent = 0;
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            SetHave();
            foreach (var (name, _, bytes, sha) in Backdrops)
            {
                cts.Token.ThrowIfCancellationRequested();
                var dest = PathOf(name);
                if (AtPinnedSize(dest, bytes)) continue;
                var r = await fetch(MilieuAssets.BaseUrls(BackdropsTag), RemoteName(name), bytes, sha, dest, null, cts.Token).ConfigureAwait(false);
                if (r.Ok)
                {
                    SetHave();
                    Interlocked.Increment(ref version);
                    continue;
                }
                end = PackState.Failed;
                detail = $"{name}: {r.Detail}";
                log.Warning($"[Milieu] Backdrop {name} unavailable ({r.Detail})");
                // Only an asset that is not there (404/410) is skipped; anything else stops the run, which Try again resumes.
                if (r.Failure == FetchFailure.Http && r.StatusCode is 404 or 410) { absent++; continue; }
                break;
            }
            if (end == PackState.Ready) log.Information("[Milieu] The bundled backdrops are ready.");
        }
        catch (Exception) when (cts.IsCancellationRequested)
        {
            end = PackState.Idle;   // plugin unloading: not a failure; partial .tmp files stay for resume
        }
        catch (Exception ex)
        {
            end = PackState.Failed;
            detail = ex.GetType().Name;
            log.Warning("[Milieu] Downloading the bundled backdrops failed: " + LogText.Describe(ex, dir));
        }
        finally
        {
            var have = CountHave();
            lock (gate)
            {
                running = false;
                // Unpublished: nothing on disk and every asset answered 404/410, i.e. the release tag doesn't exist yet.
                status = status with { State = end, Detail = detail, Have = have, Unpublished = end == PackState.Failed && have == 0 && absent == Backdrops.Length };
                Interlocked.Increment(ref version);
            }
        }
    }

    private void SetHave()
    {
        var have = CountHave();
        lock (gate) status = status with { Have = have };
    }

    private int CountHave() => Backdrops.Count(b => AtPinnedSize(PathOf(b.Name), b.Bytes));

    private static bool AtPinnedSize(string path, long bytes)
    {
        try { return File.Exists(path) && new FileInfo(path).Length == bytes; }
        catch (Exception) { return false; }
    }

    /// <summary>Cancels a fetch, waits up to 2 s for the run to stop, then releases the HTTP client.</summary>
    public void Dispose()
    {
        cts.Cancel();
        Task? t;
        lock (gate) t = runTask;
        try { t?.Wait(DisposeWait); }
        catch (AggregateException) { }
        downloader?.Dispose();
    }
}
