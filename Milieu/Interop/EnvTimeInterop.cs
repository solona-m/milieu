using System;
using Dalamud.Plugin.Services;
using Milieu.Backdrop;
using Milieu.Lighting;
using EnvManager = FFXIVClientStructs.FFXIV.Client.Graphics.Environment.EnvManager;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace Milieu.Interop;

/// <summary>
/// The local time of day (docs/adr/0012): reads and writes <c>EnvManager.DayTimeSeconds</c> and reads the game clock
/// (<c>Framework.ClientTime</c>: <c>EorzeaTimeOverride</c> when <c>IsEorzeaTimeOverridden</c>, else <c>EorzeaTime</c>).
/// Data reads through ClientStructs' resolved static-address pointers only (FFXIVClientStructs
/// Client/Graphics/Environment/EnvManager.cs and Client/System/Framework/Framework.cs, both <c>[StaticAddress]</c>
/// pointers): no game function is called, and the generated <c>Instance()</c> (which throws when unresolved) is never used.
/// A null outer pointer means the address never resolved (unavailable this session); a null inner pointer means the
/// object isn't created yet (null reads, no latch). Framework thread only.
/// </summary>
internal sealed unsafe class EnvTimeInterop(IFramework framework, ILogSink log) : IEnvTime
{
    public bool Available => EnvManager.StaticAddressPointers.ppInstance != null && GameFramework.StaticAddressPointers.ppInstance != null;

    public float? ReadDayTimeSeconds()
    {
        if (!OnFrameworkThread("read")) return null;
        var env = Env();
        return env == null ? null : env->DayTimeSeconds;
    }

    public float? ReadEorzeaDaySeconds()
    {
        if (!OnFrameworkThread("read the clock")) return null;
        var pp = GameFramework.StaticAddressPointers.ppInstance;
        if (pp == null || *pp == null) return null;
        var clock = (*pp)->ClientTime;
        var t = clock.IsEorzeaTimeOverridden ? clock.EorzeaTimeOverride : clock.EorzeaTime;
        return (float)(((t % 86400) + 86400) % 86400);
    }

    public bool WriteDayTimeSeconds(float seconds)
    {
        if (!float.IsFinite(seconds) || seconds < 0 || seconds >= NightTime.DayLength) return false;
        if (!OnFrameworkThread("write")) return false;
        var env = Env();
        if (env == null) return false;
        env->DayTimeSeconds = seconds;
        return true;
    }

    private static EnvManager* Env()
    {
        var pp = EnvManager.StaticAddressPointers.ppInstance;
        return pp == null ? null : *pp;
    }

    private bool OnFrameworkThread(string what)
    {
        if (framework.IsInFrameworkUpdateThread) return true;
        log.Debug($"[Milieu] Time of day: refused to {what} off the framework thread.");
        return false;
    }
}
