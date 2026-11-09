using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Milieu.Backdrop;
using Milieu.Imaging;

namespace Milieu.Tests.Support;

public sealed class FakeLog : ILogSink
{
    public readonly List<(string Level, string Text)> Lines = [];
    public void Debug(string message) { lock (Lines) Lines.Add(("Debug", message)); }
    public void Information(string message) { lock (Lines) Lines.Add(("Information", message)); }
    public void Warning(string message) { lock (Lines) Lines.Add(("Warning", message)); }
    public void Error(string message) { lock (Lines) Lines.Add(("Error", message)); }
}

public sealed class FakeSpawner : IVfxSpawner
{
    public bool Available { get; set; } = true;
    public readonly List<(string Path, Vector3 Pos, float Yaw, VfxHandle Handle)> Spawns = [];
    public readonly List<(VfxHandle Handle, Vector3 Pos, float Yaw)> Moves = [];
    public readonly List<VfxHandle> Removes = [];
    public readonly List<string> Calls = [];
    /// <summary>Optional log shared with FakePenumbra, to assert the order of calls across both.</summary>
    public List<string>? Order;
    public int NativeCalls => Spawns.Count + Moves.Count + Removes.Count;
    public int Ticks;
    public Func<DateTime> Clock = () => DateTime.UtcNow;
    public event Action<VfxHandle>? GameRemoved;

    public VfxHandle? Spawn(string gamePath, Vector3 position, float yawRadians)
    {
        if (!Available) throw new InvalidOperationException("native call while unavailable");
        var h = new VfxHandle(Spawns.Count + 1, gamePath, Clock());
        Spawns.Add((gamePath, position, yawRadians, h));
        Calls.Add("Spawn");
        Order?.Add("Spawn");
        return h;
    }

    public void Move(VfxHandle handle, Vector3 position, float yawRadians)
    {
        if (handle.IsDead) throw new InvalidOperationException("Move on a dead handle");
        Moves.Add((handle, position, yawRadians));
        Calls.Add("Move");
    }

    public bool Refuse;

    public bool Remove(VfxHandle handle)
    {
        if (Refuse) return false;
        if (!handle.MarkDead()) throw new InvalidOperationException("Remove on a dead handle");
        Removes.Add(handle);
        Calls.Add("Remove");
        Order?.Add("Remove");
        return true;
    }

    public void Tick() => Ticks++;

    /// <summary>Simulates the StaticVfxRemove hook firing for a handle.</summary>
    public void GameFrees(VfxHandle h)
    {
        h.MarkDead();
        GameRemoved?.Invoke(h);
    }
}

public sealed class FakePenumbra : IPenumbraRedirects, IPenumbraSync
{
    public PenumbraState State { get; set; } = PenumbraState.Available;
    public Dictionary<string, string>? Current;
    public readonly List<IReadOnlyDictionary<string, string>> Sets = [];
    public int Clears;
    public int Refreshes;
    public bool ResolveWorks = true;
    public bool SetSucceeds = true;
    public readonly List<string> Calls = [];
    public List<string>? Order;

    public void Refresh() => Refreshes++;

    public bool Set(IReadOnlyDictionary<string, string> redirects)
    {
        Calls.Add("Set");
        Order?.Add("Set");
        if (!SetSucceeds) return false;
        Sets.Add(redirects);
        Current = new Dictionary<string, string>(redirects);
        return true;
    }

    public void Clear() { Calls.Add("Clear"); Order?.Add("Clear"); Clears++; Current = null; }

    public string Resolve(string gamePath) =>
        ResolveWorks && Current != null && Current.TryGetValue(gamePath, out var disk) ? disk : gamePath;

    // ---------------------------------------------------------------- IPenumbraSync

    public string? ModDirectory { get; set; }
    public PlayerCollectionInfo? Player = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), true);
    public Guid? Base = Guid.Parse("00000000-0000-0000-0000-0000000000ba");
    public Guid[] Listed = [];
    public int SetSyncedResult;
    public bool SetSyncedThrows;
    public bool ResolvePlayerWorks = true;
    public string Meta = "";
    public int MetaReads;
    public int Redraws;
    /// <summary>Every synced call, in order: "SetSynced", "ClearSynced", "Redraw", "ClearEverywhere", "Meta".</summary>
    public readonly List<string> SyncCalls = [];
    public readonly List<(Guid Collection, IReadOnlyDictionary<string, string> Paths, string Manips)> SyncSets = [];
    public readonly List<Guid> SyncClears = [];
    public readonly List<Guid[]> ClearEverywhereCalls = [];
    public readonly Dictionary<Guid, Dictionary<string, string>> Synced = [];

    public PlayerCollectionInfo? PlayerCollection() => Player;

    public Guid? BaseCollection() => Base;

    public int SetSynced(Guid collection, IReadOnlyDictionary<string, string> paths, string manips)
    {
        SyncCalls.Add("SetSynced");
        Order?.Add("SetSynced");
        if (SetSyncedThrows) return -1;   // the real wrapper maps a throw to -1
        if (SetSyncedResult != 0) return SetSyncedResult;
        SyncSets.Add((collection, paths, manips));
        Synced[collection] = new Dictionary<string, string>(paths);
        return 0;
    }

    public void ClearSynced(Guid collection)
    {
        SyncCalls.Add("ClearSynced");
        Order?.Add("ClearSynced");
        SyncClears.Add(collection);
        Synced.Remove(collection);
    }

    public void ClearSyncedEverywhere(IEnumerable<Guid> alsoClear)
    {
        var also = alsoClear.ToArray();
        SyncCalls.Add("ClearEverywhere");
        ClearEverywhereCalls.Add(also);
        foreach (var id in Listed.Concat(also).Distinct()) Synced.Remove(id);
    }

    public string ResolvePlayer(string gamePath) =>
        ResolvePlayerWorks && Player is { } p && Synced.TryGetValue(p.Id, out var set) && set.TryGetValue(gamePath, out var disk) ? disk : gamePath;

    public string PlayerMeta() { MetaReads++; SyncCalls.Add("Meta"); return Meta; }

    public void RedrawPlayer() { Redraws++; SyncCalls.Add("Redraw"); Order?.Add("Redraw"); }

    /// <summary>The synced calls that change state (no reads, no startup cleanup).</summary>
    public IEnumerable<string> RegistrationCalls => SyncCalls.Where(c => c is "SetSynced" or "ClearSynced" or "Redraw");
}

/// <summary>Vanilla game files by path.</summary>
public sealed class FakeGameData : IGameData
{
    public readonly Dictionary<string, byte[]> Files = [];
    public int Reads;
    public byte[]? ReadVanilla(string gamePath) { Reads++; return Files.TryGetValue(gamePath, out var d) ? d : null; }
}

public sealed class FakeGame : IGameState
{
    public bool IsLoggedIn { get; set; } = true;
    public bool Ready = true;
    public bool InCutscene;
    public bool BetweenAreas { get; set; }
    public bool IsReady => IsLoggedIn && Ready && !BetweenAreas && !InCutscene && PlayerPosition != null;
    public Vector3? PlayerPosition { get; set; } = new Vector3(10, 20, 30);
    public bool IsGPosing { get; set; }
}

/// <summary>Writes a tiny real texture file into the cache so the controller has something to register.</summary>
public sealed class FakePreparer(CacheStore cache) : IBackdropPreparer
{
    public int Calls;
    public TaskCompletionSource<PreparedTexture>? Gate;
    public Exception? Throw;
    public readonly List<int> Threads = [];
    public CancellationToken LastToken;
    public IProgress<float>? LastProgress;

    public Task<PreparedTexture> PrepareTextureAsync(BackdropRequest request, IProgress<float>? progress, CancellationToken ct)
    {
        Calls++;
        Threads.Add(Environment.CurrentManagedThreadId);
        LastToken = ct;
        LastProgress = progress;
        if (Throw != null) return Task.FromException<PreparedTexture>(Throw);
        if (Gate != null) return Gate.Task;
        return Task.FromResult(Make(cache, request.ImagePath));
    }

    /// <summary>Scene lighting's rig loader: null (no rig) unless a test sets <see cref="Lights"/>.</summary>
    public Func<string, CancellationToken, Task<Milieu.Lighting.LightRig?>>? Lights;
    public int LightCalls;

    public Task<Milieu.Lighting.LightRig?> PrepareLightsAsync(string imagePath, CancellationToken ct)
    {
        LightCalls++;
        return Lights?.Invoke(imagePath, ct) ?? Task.FromResult<Milieu.Lighting.LightRig?>(null);
    }

    public static PreparedTexture Make(CacheStore cache, string path, string key = "00112233445566ff")
    {
        System.IO.Directory.CreateDirectory(cache.Root);
        var disk = cache.TextureDiskPath(key);
        System.IO.File.WriteAllBytes(disk, [1, 2, 3]);
        return new PreparedTexture(key, disk, CacheStore.TextureGamePath(key), 1024, 512, ImageKind.Jpeg, []);
    }
}

public sealed class FakeGridController : Milieu.Gui.IGridController
{
    public BackdropState State { get; set; } = BackdropState.Hidden;
    public float Progress { get; set; }
    public readonly List<string> Calls = [];
    public StatusMessage? Reported;
    public void Show() => Calls.Add("Show");
    public void Hide() => Calls.Add("Hide");
    public void ReportStatus(StatusMessage message) { Calls.Add("Report"); Reported = message; }
}

public sealed class FakeGridDownloads : Milieu.Gui.IGridDownloads
{
    public readonly HashSet<string> Downloaded = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, Milieu.Assets.EntryStatus> Status = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Requests = [];
    public bool Unpublished;

    public Milieu.Assets.EntryStatus StatusOf(string stem) =>
        Status.TryGetValue(stem, out var s) ? s
        : new Milieu.Assets.EntryStatus(Downloaded.Contains(stem) ? Milieu.Assets.EntryState.Downloaded : Milieu.Assets.EntryState.NotDownloaded, 0, "");
    public bool IsDownloaded(string stem) => Downloaded.Contains(stem);
    public void Request(string stem) => Requests.Add(stem);
    public void RequestAll() => Requests.Add("*");
    public bool AnyMissing => Downloaded.Count < Milieu.Assets.BackdropPins.Count;
    public bool PackUnpublished => Unpublished;
}

/// <summary>Bundled scenes on disk for the preparer/controller tests.</summary>
public sealed class FakeBundled(string dir) : Milieu.Assets.IBundledBackdrops
{
    public readonly List<string> Corrupt = [];
    public bool IsDownloaded(string stem) => System.IO.File.Exists(PathOf(stem));
    public string PathOf(string stem) => System.IO.Path.Combine(dir, stem + ".atex");
    public void MarkCorrupt(string stem) { Corrupt.Add(stem); System.IO.File.Delete(PathOf(stem)); }
}

/// <summary>Records every light call. Throws when used while unavailable or on a dead handle (a real call there could crash the game).</summary>
public sealed class FakeLightSpawner : Milieu.Lighting.ILightSpawner
{
    public bool Available { get; set; } = true;
    public readonly List<(Milieu.Lighting.LightParams P, Milieu.Lighting.LightHandle H)> Creates = [];
    public readonly List<(Milieu.Lighting.LightHandle H, Milieu.Lighting.LightParams P)> Updates = [];
    public readonly List<Milieu.Lighting.LightHandle> Refreshes = [];
    public readonly List<Milieu.Lighting.LightHandle> Destroys = [];
    /// <summary>Every call in order, refused and failed ones included: "Create", "Update", "Refresh", "Destroy".</summary>
    public readonly List<string> Calls = [];
    /// <summary>1-based number of the Create call that returns null (0: none).</summary>
    public int FailCreateAt;
    public int CreateAttempts;
    /// <summary>Handles the game "freed": Update/Refresh find them dead.</summary>
    public readonly HashSet<Milieu.Lighting.LightHandle> GoneByGame = [];
    public bool RefuseDestroy;
    private int next = 0x1000;

    public int NativeCalls => Calls.Count;

    public IEnumerable<Milieu.Lighting.LightHandle> Live => Creates.Select(c => c.H).Where(h => !h.IsDead);

    private void Check(Milieu.Lighting.LightHandle? h, string what)
    {
        if (!Available) throw new InvalidOperationException($"{what} while unavailable");
        if (h is { IsDead: true }) throw new InvalidOperationException($"{what} on a dead handle");
        Calls.Add(what);
    }

    public Milieu.Lighting.LightHandle? Create(Milieu.Lighting.LightParams p)
    {
        Check(null, "Create");
        if (++CreateAttempts == FailCreateAt) return null;
        var h = new Milieu.Lighting.LightHandle(next += 0x10);
        Creates.Add((p, h));
        return h;
    }

    public bool Update(Milieu.Lighting.LightHandle h, Milieu.Lighting.LightParams p)
    {
        Check(h, "Update");
        if (GoneByGame.Contains(h)) { h.MarkDead(); return false; }
        Updates.Add((h, p));
        return true;
    }

    public bool Refresh(Milieu.Lighting.LightHandle h)
    {
        Check(h, "Refresh");
        if (GoneByGame.Contains(h)) { h.MarkDead(); return false; }
        Refreshes.Add(h);
        return true;
    }

    public bool Destroy(Milieu.Lighting.LightHandle h)
    {
        Check(h, "Destroy");
        if (RefuseDestroy) return false;
        h.MarkDead();
        Destroys.Add(h);
        return true;
    }
}

public sealed class FakeSentinel : Milieu.Lighting.ILightingSentinel
{
    public bool WasSetAtStartup { get; init; }
    public bool IsSet;
    public int Sets, Clears;
    public readonly List<string> Calls = [];
    public FakeSentinel(bool setAtStartup = false) { WasSetAtStartup = setAtStartup; IsSet = setAtStartup; }
    public void Set() { Sets++; IsSet = true; Calls.Add("Set"); }
    public void Clear() { Clears++; IsSet = false; Calls.Add("Clear"); }
}
