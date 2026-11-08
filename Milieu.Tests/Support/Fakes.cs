using System;
using System.Collections.Generic;
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
        return h;
    }

    public void Move(VfxHandle handle, Vector3 position, float yawRadians)
    {
        if (handle.IsDead) throw new InvalidOperationException("Move on a dead handle");
        Moves.Add((handle, position, yawRadians));
        Calls.Add("Move");
    }

    public void Remove(VfxHandle handle)
    {
        if (!handle.MarkDead()) throw new InvalidOperationException("Remove on a dead handle");
        Removes.Add(handle);
        Calls.Add("Remove");
    }

    public void Tick() => Ticks++;

    /// <summary>Simulates the StaticVfxRemove hook firing for a handle.</summary>
    public void GameFrees(VfxHandle h)
    {
        h.MarkDead();
        GameRemoved?.Invoke(h);
    }
}

public sealed class FakePenumbra : IPenumbraRedirects
{
    public PenumbraState State { get; set; } = PenumbraState.Available;
    public Dictionary<string, string>? Current;
    public readonly List<IReadOnlyDictionary<string, string>> Sets = [];
    public int Clears;
    public int Refreshes;
    public bool ResolveWorks = true;
    public bool SetSucceeds = true;
    public readonly List<string> Calls = [];

    public void Refresh() => Refreshes++;

    public bool Set(IReadOnlyDictionary<string, string> redirects)
    {
        Calls.Add("Set");
        if (!SetSucceeds) return false;
        Sets.Add(redirects);
        Current = new Dictionary<string, string>(redirects);
        return true;
    }

    public void Clear() { Calls.Add("Clear"); Clears++; Current = null; }

    public string Resolve(string gamePath) =>
        ResolveWorks && Current != null && Current.TryGetValue(gamePath, out var disk) ? disk : gamePath;
}

public sealed class FakeGame : IGameState
{
    public bool IsLoggedIn { get; set; } = true;
    public bool Ready = true;
    public bool InCutscene;
    public bool BetweenAreas { get; set; }
    public bool IsReady => IsLoggedIn && Ready && !BetweenAreas && !InCutscene && PlayerPosition != null;
    public Vector3? PlayerPosition { get; set; } = new Vector3(10, 20, 30);
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

    public static PreparedTexture Make(CacheStore cache, string path, string key = "00112233445566ff")
    {
        System.IO.Directory.CreateDirectory(cache.Root);
        var disk = cache.TextureDiskPath(key);
        System.IO.File.WriteAllBytes(disk, [1, 2, 3]);
        return new PreparedTexture(key, disk, CacheStore.TextureGamePath(key), 1024, 512, ImageKind.Jpeg, []);
    }
}
