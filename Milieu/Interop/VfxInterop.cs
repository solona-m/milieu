using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Milieu.Backdrop;

namespace Milieu.Interop;

/// <summary>
/// Spawns, moves and removes static VFX the way VFXEditor's StaticVfx does: VfxObject.Create(path, pool), then
/// StaticVfxRun(vfx, 0, 0xFFFFFFFF), then position/rotation and UpdateTransforms. A hook on StaticVfxRemove marks
/// Milieu's handle dead when the game frees the dome itself, so Milieu never touches a freed VFX.
/// Every call except the hook detour must come from the framework thread; calls from elsewhere are refused.
/// </summary>
public sealed unsafe class VfxInterop : IVfxSpawner, IDisposable
{
    private delegate nint StaticVfxRunDelegate(VfxObject* vfx, float a1, uint a2);
    private delegate nint StaticVfxRemoveDelegate(VfxObject* vfx);

    private readonly IFramework framework;
    private readonly ILogSink log;
    private readonly NativeStringPool strings;
    private readonly VfxObject.Delegates.Create? create;
    private readonly StaticVfxRunDelegate? run;
    private readonly Hook<StaticVfxRemoveDelegate>? removeHook;
    private readonly ConcurrentDictionary<nint, (VfxHandle Handle, nint PathString)> live = new();
    private readonly ConcurrentQueue<nint> freedByGame = new();

    public bool Available { get; }

    public event Action<VfxHandle>? GameRemoved;

    public VfxInterop(ISigScanner scanner, IGameInteropProvider hooks, IFramework framework, ILogSink log, NativeStringPool strings)
    {
        this.framework = framework;
        this.log = log;
        this.strings = strings;
        try
        {
            if (!scanner.TryScanText(VfxObject.Addresses.Create.String, out var createAddr)
                || !scanner.TryScanText(Signatures.StaticVfxRun, out var runAddr)
                || !scanner.TryScanText(Signatures.StaticVfxRemove, out var removeAddr))
            {
                log.Warning("[Milieu] A static VFX signature was not found; backdrops are disabled until Milieu is updated.");
                return;
            }
            create = Marshal.GetDelegateForFunctionPointer<VfxObject.Delegates.Create>(createAddr);
            run = Marshal.GetDelegateForFunctionPointer<StaticVfxRunDelegate>(runAddr);
            removeHook = hooks.HookFromAddress<StaticVfxRemoveDelegate>(removeAddr, RemoveDetour);
            removeHook.Enable();
            Available = true;
        }
        catch (Exception e)
        {
            log.Warning("[Milieu] Could not set up VFX interop: " + LogText.Describe(e));
            Available = false;
        }
    }

    private bool OnFrameworkThread(string what)
    {
        if (framework.IsInFrameworkUpdateThread) return true;
        log.Error($"[Milieu] {what} refused: not on the framework thread.");
        return false;
    }

    public VfxHandle? Spawn(string gamePath, Vector3 position, float yawRadians)
    {
        if (!Available || create == null || run == null || !OnFrameworkThread("Spawn")) return null;
        strings.FreePending();
        var path = strings.Allocate(gamePath);
        var vfx = create((byte*)path, (byte*)strings.Pool);
        if (vfx == null)
        {
            strings.ScheduleFree(path);
            log.Warning("[Milieu] The game did not create the dome VFX.");
            return null;
        }
        run(vfx, 0f, 0xFFFFFFFF);
        var handle = new VfxHandle((nint)vfx, gamePath, DateTime.UtcNow);
        live[(nint)vfx] = (handle, path);
        Place(vfx, position, yawRadians);
        log.Debug("[Milieu] Dome VFX spawned.");
        return handle;
    }

    public void Move(VfxHandle handle, Vector3 position, float yawRadians)
    {
        if (handle.IsDead || !OnFrameworkThread("Move") || !live.ContainsKey(handle.Pointer)) return;
        Place((VfxObject*)handle.Pointer, position, yawRadians);
    }

    private static void Place(VfxObject* vfx, Vector3 position, float yaw)
    {
        vfx->Position = position;
        vfx->Rotation = FFXIVClientStructs.FFXIV.Common.Math.Quaternion.CreateFromYawPitchRoll(yaw, 0, 0);
        vfx->UpdateTransforms(true);
    }

    public void Remove(VfxHandle handle)
    {
        if (!OnFrameworkThread("Remove")) return;
        if (!handle.MarkDead()) return;                    // already removed, by Milieu or by the game
        if (!live.TryRemove(handle.Pointer, out var entry)) return;
        removeHook?.Original((VfxObject*)handle.Pointer);  // straight to the game: our detour need not see it
        strings.ScheduleFree(entry.PathString);
    }

    /// <summary>Runs on whatever thread the game frees VFX on. Fast, never throws, always calls the original.</summary>
    private nint RemoveDetour(VfxObject* vfx)
    {
        try
        {
            if (live.TryRemove((nint)vfx, out var entry) && entry.Handle.MarkDead())
            {
                freedByGame.Enqueue(entry.PathString);
                GameRemoved?.Invoke(entry.Handle);
            }
        }
        catch (Exception)
        {
            // Never let an exception escape into the game.
        }
        return removeHook!.Original(vfx);
    }

    public void Tick()
    {
        while (freedByGame.TryDequeue(out var p)) strings.ScheduleFree(p);
        strings.Tick();
    }

    /// <summary>Disposes the hook. The caller removes the dome first and then frees or leaks the strings.</summary>
    public void Dispose()
    {
        removeHook?.Dispose();
    }
}
