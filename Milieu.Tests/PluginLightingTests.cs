using System;
using System.Threading.Tasks;
using Milieu.Lighting;
using Milieu.Sync;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public class PluginLightingTests
{
    [Fact] // [AC-scene-lighting-10] [EDGE-scene-lighting-28]
    public void CreateLighting_KillSwitchOff_ReturnsNull_NeverCallsSpawnerFactory()
    {
        var spawners = 0;
        var sentinels = 0;
        var loads = 0;
        SceneLighting? Make(bool available) => Plugin.CreateLighting(available,
            () => { spawners++; return new FakeLightSpawner(); }, new FakeGame(), () => null, new Configuration { SceneLighting = true },
            (_, _) => { loads++; return Task.FromResult<LightRig?>(null); },
            () => { sentinels++; return new FakeSentinel(); }, new FakeLog(), () => false, () => DeliveryMode.Local, () => true);

        Assert.Null(Make(false));
        Assert.Equal((0, 0, 0), (spawners, sentinels, loads));   // nothing resolved, nothing read

        var on = Make(true);
        Assert.NotNull(on);
        Assert.Equal((1, 1), (spawners, sentinels));
        on!.Tick();
        Assert.Equal(LightingStatus.WaitingForBackdrop, on.Status.Status);
    }

    [Fact] // [EDGE-scene-lighting-23]
    public void CombineJobs_WaitsForConversionAndRigJob()
    {
        Assert.Null(Plugin.CombineJobs(null, null));
        var a = new TaskCompletionSource();
        var b = new TaskCompletionSource();
        var both = Plugin.CombineJobs(a.Task, b.Task)!;
        a.SetResult();
        Assert.False(both.IsCompleted);
        b.SetResult();
        Assert.True(both.Wait(TimeSpan.FromSeconds(1)));
        Assert.NotNull(Plugin.CombineJobs(a.Task, null));
    }

    [Fact] // [EDGE-synced-lighting-40] [EDGE-synced-lighting-1]
    public void SyncedRigLoader_OnlyWhenBothSwitchesAreOn()
    {
        System.Func<string, System.Threading.CancellationToken, Task<LightRig?>> load = (_, _) => Task.FromResult<LightRig?>(null);
        Assert.Same(load, Plugin.SyncedRigLoader(true, true, load));
        Assert.Null(Plugin.SyncedRigLoader(true, false, load));
        Assert.Null(Plugin.SyncedRigLoader(false, true, load));
    }

    [Theory] // [EDGE-synced-lighting-40] [AC-synced-lighting-4]
    [InlineData(true, LightingStatus.InSyncedBackdrop)]
    [InlineData(false, LightingStatus.WaitingForGPose)]
    public void CreateLighting_PassesTheSyncedLightsSwitch(bool syncedLightsOn, LightingStatus expected)
    {
        var lit = new LitBackdrop(@"C:\sky.hdr", new DomeView(0, false, 1, 2, 1), Synced: true, VfxLightCount: 1);
        var s = Plugin.CreateLighting(true, () => new FakeLightSpawner(), new FakeGame(), () => lit, new Configuration { SceneLighting = true },
            (_, _) => Task.FromResult<LightRig?>(null), () => new FakeSentinel(), new FakeLog(), () => false, () => DeliveryMode.MadBirdRing,
            () => syncedLightsOn)!;
        s.Tick();
        Assert.Equal(expected, s.Status.Status);
    }
    [Fact] // [EDGE-local-dome-lights-14] [EDGE-local-dome-lights-15]
    public void LocalRigLoader_OnlyWhenBothSwitchesAreOn()
    {
        System.Func<string, System.Threading.CancellationToken, Task<LightRig?>> load = (_, _) => Task.FromResult<LightRig?>(null);
        Assert.Same(load, Plugin.LocalRigLoader(true, true, load));
        Assert.Null(Plugin.LocalRigLoader(true, false, load));
        Assert.Null(Plugin.LocalRigLoader(false, true, load));
    }

    [Fact] // [EDGE-local-dome-lights-16]
    public void LocalRigLoader_IndependentOfSyncedSwitch()
    {
        System.Func<string, System.Threading.CancellationToken, Task<LightRig?>> load = (_, _) => Task.FromResult<LightRig?>(null);
        Assert.Null(Plugin.SyncedRigLoader(true, false, load));
        Assert.Same(load, Plugin.LocalRigLoader(true, true, load));
    }
}
