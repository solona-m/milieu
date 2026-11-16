using System;
using System.Collections.Generic;
using Milieu.Backdrop;
using Milieu.Lighting;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

/// <summary>The plugin's night wiring seams, with a real BackdropController where Hidden matters (docs/adr/0012).</summary>
public sealed class PluginWiringTests : IDisposable
{
    private readonly TempDir dir = new();
    private readonly FakeEnvTime env = new();
    private readonly FakeGame game = new();
    private readonly FakePenumbra pen = new();
    private readonly Configuration config = new() { NightWhileShown = true };
    private readonly BackdropController c;
    private readonly NightWhileShown night;

    public PluginWiringTests()
    {
        var cache = new CacheStore(dir.File("cache"));
        config.ImagePath = dir.File("pano.jpg");
        c = new BackdropController(new FakeSpawner(), pen, game, new KeyedPreparer(cache), cache, config, new FakeLog(), run: f => f());
        night = new NightWhileShown(env, game, () => c.IsDrawn, config, new FakeSentinel(), new FakeLog(), () => false);
    }

    public void Dispose() => dir.Dispose();

    private void ShowAndNight()
    {
        c.Show(); c.Tick(); c.Tick();
        Assert.True(c.IsDrawn);
        night.Tick();
        Assert.Equal(NightStatus.On, night.Status);
    }

    [Theory] // [EDGE-synced-lighting-28]
    [InlineData("Hide")]
    [InlineData("PenumbraDisposed")]
    public void Wire_HiddenRestoresTheTimeWithinTheCall(string how)
    {
        Plugin.Wire(c, null, night);
        ShowAndNight();
        env.Clock = 50500f;
        if (how == "Hide") c.Hide(); else c.OnPenumbraDisposed();
        Assert.Equal(50500f, env.Writes[^1]);   // before any further tick
    }

    [Fact] // [EDGE-synced-lighting-28]
    public void Unwire_Detaches()
    {
        Plugin.Wire(c, null, night);
        Plugin.Unwire(c, null, night);
        ShowAndNight();
        var writes = env.Writes.Count;
        c.Hide();
        Assert.Equal(writes, env.Writes.Count);
    }

    [Fact] // [AC-synced-lighting-5] [EDGE-synced-lighting-22]
    public void Logout_NothingWrittenDuringIt_RestoredOnTheFirstReadyTick()
    {
        Plugin.Wire(c, null, night);
        ShowAndNight();
        var writes = env.Writes.Count;
        env.Clock = 50600f;
        game.IsLoggedIn = false;
        Plugin.OnZoneOrLogout(c.OnLogout, null, night, zone: false);
        Assert.Equal(writes, env.Writes.Count);   // the controller's Hide didn't restore during the logout
        game.IsLoggedIn = true;
        night.Tick();
        Assert.Equal(50600f, env.Writes[^1]);
    }

    [Fact] // [EDGE-synced-lighting-21]
    public void ZoneChange_NothingWrittenNow_RestoredOnTheFirstReadyTick()
    {
        Plugin.Wire(c, null, night);
        ShowAndNight();
        var writes = env.Writes.Count;
        env.Clock = 50700f;
        Plugin.OnZoneOrLogout(c.OnTerritoryChanged, null, night, zone: true);
        Assert.Equal(writes, env.Writes.Count);
        night.Tick();
        Assert.Contains(50700f, env.Writes);
    }

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
    }

    [Fact] // [EDGE-synced-lighting-27]
    public void TeardownOrder_LightingNightController()
    {
        var order = new List<string>();
        Plugin.TeardownOrder(() => order.Add("lighting"), () => order.Add("night"), () => order.Add("controller"));
        Assert.Equal(["lighting", "night", "controller"], order);
        order.Clear();
        Plugin.TeardownOrder(null, null, () => order.Add("controller"));
        Assert.Equal(["controller"], order);
    }
}
