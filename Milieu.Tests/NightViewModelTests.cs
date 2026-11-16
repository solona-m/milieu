using System.Collections.Generic;
using Milieu.Backdrop;
using Milieu.Gui;
using Milieu.Lighting;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public class NightViewModelTests
{
    [Theory] // [AC-synced-lighting-5] [AC-synced-lighting-6]
    [InlineData(NightStatus.On, "", Severity.Info)]
    [InlineData(NightStatus.Off, "", Severity.Info)]
    [InlineData(NightStatus.Waiting, "", Severity.Info)]
    [InlineData(NightStatus.NotInThisZone, NightViewModel.NotInThisZoneText, Severity.Info)]
    [InlineData(NightStatus.Unavailable, NightViewModel.UnavailableText, Severity.Error)]
    [InlineData(NightStatus.CrashGuard, NightViewModel.CrashGuardText, Severity.Warning)]
    public void StatusTexts(NightStatus status, string text, Severity severity)
    {
        var vm = new NightViewModel(status, true);
        Assert.Equal(text, vm.StatusText);
        Assert.Equal(severity, vm.StatusSeverity);
    }

    [Fact] // [EDGE-synced-lighting-26]
    public void KillSwitchOff_Hidden()
    {
        Assert.False(new NightViewModel(NightStatus.Off, false).Visible);
        Assert.True(new NightViewModel(NightStatus.Off, true).Visible);
    }

    [Fact] // [AC-synced-lighting-5]
    public void Change_SetsAndSaves()
    {
        var config = new Configuration();
        var saves = 0;
        NightViewModel.Change(config, true, () => saves++)();
        Assert.True(config.NightWhileShown);
        Assert.Equal(1, saves);
    }
}

public class PluginNightTests
{
    [Fact] // [AC-synced-lighting-6] [EDGE-synced-lighting-26]
    public void CreateNight_KillSwitchOff_ReturnsNull_NeverCallsFactories()
    {
        int envs = 0, sentinels = 0;
        NightWhileShown? Make(bool available) => Plugin.CreateNight(available, () => { envs++; return new FakeEnvTime(); }, new FakeGame(),
            () => true, new Configuration(), () => { sentinels++; return new FakeSentinel(); }, new FakeLog(), () => false);
        Assert.Null(Make(false));
        Assert.Equal((0, 0), (envs, sentinels));
        Assert.NotNull(Make(true));
        Assert.Equal((1, 1), (envs, sentinels));
    }

    [Fact]
    public void TickAll_ControllerThenLightingThenNight()
    {
        var order = new List<string>();
        Plugin.TickAll(() => order.Add("controller"), () => order.Add("lighting"), () => order.Add("night"));
        Assert.Equal(["controller", "lighting", "night"], order);
        order.Clear();
        Plugin.TickAll(() => order.Add("controller"), null, null);
        Assert.Equal(["controller"], order);
    }

    [Theory] // [EDGE-synced-lighting-21] [EDGE-synced-lighting-22]
    [InlineData(true)]
    [InlineData(false)]
    public void ZoneOrLogout_ForgetsTheNight_NoWrite(bool zone)
    {
        var env = new FakeEnvTime();
        var game = new FakeGame();
        var night = new NightWhileShown(env, game, () => true, new Configuration { NightWhileShown = true }, new FakeSentinel(), new FakeLog(), () => false);
        night.Tick();
        var writes = env.Writes.Count;
        Plugin.OnZoneOrLogout(null, night, zone);
        Assert.Equal(writes, env.Writes.Count);   // nothing written now; the next ready tick restores
    }
}
