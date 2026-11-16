using System;
using System.Linq;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Milieu.Backdrop;
using Milieu.Lighting;
using EnvManager = FFXIVClientStructs.FFXIV.Client.Graphics.Environment.EnvManager;

namespace Milieu.Interop;

/// <summary>
/// The game's environment update (docs/adr/0013): <c>UpdateEnvironment(EnvState* a1, EnvState* a2)</c>, a general EnvState
/// copy at <see cref="Signatures.UpdateEnvironment"/> (Brio Game/World/EnvironmentService.cs). The constructor only scans
/// (exactly one match). <see cref="Install"/> creates and enables the hook on the first activation and it stays enabled as a
/// pass-through until <see cref="Dispose"/>. The detour calls the original, then <see cref="SunOffPostStep.Run"/>: no
/// closure, no allocation, no logging on the game's thread. EnvManager's address is a data read through ClientStructs'
/// resolved static pointer (null-checked; the generated <c>Instance()</c> is never used).
/// </summary>
internal sealed unsafe class SunOffInterop : ISunSwitch, IDisposable
{
    private delegate nint UpdateEnvironmentDelegate(void* a1, void* a2);

    private readonly IGameInteropProvider hooks;
    private readonly IFramework framework;
    private readonly ILogSink log;
    private readonly nint address;
    private readonly SunOffShared shared = new();
    private Hook<UpdateEnvironmentDelegate>? hook;
    private bool? installResult;

    public bool Available { get; }

    public SunOffShared Shared => shared;

    public SunOffInterop(ISigScanner scanner, IGameInteropProvider hooks, IFramework framework, ILogSink log)
    {
        this.hooks = hooks;
        this.framework = framework;
        this.log = log;
        try
        {
            if (SunOffScan.PickAddress(scanner.ScanAllText(Signatures.UpdateEnvironment).Take(2)) is not { } found)
            {
                log.Warning("[Milieu] The game's lighting update wasn't found exactly once; sun off is disabled until Milieu is updated.");
                return;
            }
            if (EnvManager.StaticAddressPointers.ppInstance == null)
            {
                log.Warning("[Milieu] The game's environment manager wasn't found; sun off is disabled until Milieu is updated.");
                return;
            }
            address = found;
            Available = true;
        }
        catch (Exception e)
        {
            log.Warning("[Milieu] Could not set up sun off: " + LogText.Describe(e));
            Available = false;
        }
    }

    public bool Install()
    {
        if (!framework.IsInFrameworkUpdateThread)
        {
            log.Error("[Milieu] Sun off: refused to hook off the framework thread.");
            return false;
        }
        if (shared.Closed) return false;   // disposed: never hook again
        if (installResult is { } done) return done;
        if (!Available) { installResult = false; return false; }
        try
        {
            // The field is assigned before Enable(): the detour can only run once enabled, so it always finds the hook.
            hook = hooks.HookFromAddress<UpdateEnvironmentDelegate>(address, Detour);
            hook.Enable();
            installResult = true;
            log.Information("[Milieu] Sun off: hooked the game's lighting update.");
        }
        catch (Exception e)
        {
            log.Warning("[Milieu] Sun off: the game's lighting update couldn't be hooked: " + LogText.Describe(e));
            // Unpatch first, then clear the field: a detour already running keeps reaching Original until the hook is gone.
            hook?.Dispose();
            hook = null;
            installResult = false;
        }
        return installResult.Value;
    }

    private nint Detour(void* a1, void* a2)
    {
        var h = hook;
        if (h == null) return 0;   // unreachable (Install assigns before Enable); without the hook Original can't be called
        var result = h.Original(a1, a2);
        try { SunOffPostStep.Run((byte*)a1, ReadEnv(), shared); }
        catch { /* a managed bug must never escape into the game; access violations aren't caught (the address checks guard) */ }
        return result;
    }

    private static byte* ReadEnv()
    {
        var pp = EnvManager.StaticAddressPointers.ppInstance;
        return pp == null ? null : (byte*)*pp;
    }

    public void Dispose()
    {
        shared.Close();
        hook?.Dispose();
    }
}
