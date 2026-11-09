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
            () => { sentinels++; return new FakeSentinel(); }, new FakeLog(), () => false, () => DeliveryMode.Local);

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
}
