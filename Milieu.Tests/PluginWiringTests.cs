using System;
using System.Collections.Generic;
using Milieu.Backdrop;
using Milieu.Lighting;
using Milieu.Sync;
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
    private readonly FakeSunSwitch sw = new();
    private readonly SunOff sunOff;

    public PluginWiringTests()
    {
        var cache = new CacheStore(dir.File("cache"));
        config.ImagePath = dir.File("pano.jpg");
        c = new BackdropController(new FakeSpawner(), pen, game, new KeyedPreparer(cache), cache, config, new FakeLog(), run: f => f());
        night = new NightWhileShown(env, game, () => c.IsDrawn, config, new FakeSentinel(), new FakeLog(), () => false);
        sunOff = new SunOff(sw, game, () => c.Lit, () => c.EffectiveDelivery, config, new FakeSentinel(), new FakeLog(), () => false);
    }

    private void ShowAndSunOff()
    {
        c.Show(); c.Tick(); c.Tick();
        Assert.True(c.IsDrawn);
        sunOff.Tick();
        Assert.True(sw.Shared.Writing);
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

    [Fact] // [EDGE-synced-lighting-28] [AC-sun-off-2] [EDGE-sun-off-8]
    public void Unwire_Detaches()
    {
        Plugin.Wire(c, null, night, sunOff);
        Plugin.Unwire(c, null, night, sunOff);
        ShowAndNight();
        sunOff.Tick();
        Assert.True(sw.Shared.Writing);
        var writes = env.Writes.Count;
        c.Hide();
        Assert.Equal(writes, env.Writes.Count);
        Assert.True(sw.Shared.Writing);   // detached: the Hide didn't reach sun off
    }

    [Theory] // [AC-sun-off-2] [EDGE-sun-off-8]
    [InlineData("Hide")]
    [InlineData("PenumbraDisposed")]
    public void Wire_HiddenStopsSunOffWithinTheCall(string how)
    {
        Plugin.Wire(c, null, null, sunOff);
        ShowAndSunOff();
        if (how == "Hide") c.Hide(); else c.OnPenumbraDisposed();
        Assert.False(sw.Shared.Writing);   // before any further tick
    }

    [Fact] // [AC-sun-off-2] [EDGE-sun-off-9]
    public void ZoneOrLogout_StopsSunOff_RestartsOnNextReadyTick()
    {
        Plugin.Wire(c, null, night, sunOff);
        ShowAndSunOff();
        using var buf = new EnvBuffer();
        buf.SetColours(0.8f, 0.7f, 0.6f, 0f, 0f, 0f);
        buf.Post(sw.Shared);
        sunOff.Tick();
        Assert.Equal(SunOffProbe.Passed, sw.Shared.Probe);
        Plugin.OnZoneOrLogout(c.OnTerritoryChanged, null, night, zone: true, sunOff);
        Assert.False(sw.Shared.Writing);
        for (var i = 0; i < 2 * BackdropController.RespawnDelayFrames && !c.IsDrawn; i++) c.Tick();
        Assert.True(c.IsDrawn);
        sunOff.Tick();
        Assert.True(sw.Shared.Writing);
        Assert.Equal(SunOffStatus.On, sunOff.Status);   // no second capture
        Assert.Equal(1, sw.Installs);

        game.IsLoggedIn = false;
        Plugin.OnZoneOrLogout(c.OnLogout, null, night, zone: false, sunOff);
        Assert.False(sw.Shared.Writing);
    }

    [Fact] // [AC-sun-off-7] [EDGE-sun-off-3]
    public void CreateSunOff_KillSwitchOff_ReturnsNull_NeverCallsFactories()
    {
        int switches = 0, sentinels = 0;
        SunOff? Make(bool available, out ISunSwitch? created) => Plugin.CreateSunOff(available, () => { switches++; return new FakeSunSwitch(); },
            new FakeGame(), () => null, () => DeliveryMode.Local, new Configuration(), () => { sentinels++; return new FakeSentinel(); },
            new FakeLog(), () => false, out created);
        Assert.Null(Make(false, out var none));
        Assert.Null(none);
        Assert.Equal((0, 0), (switches, sentinels));
        Assert.NotNull(Make(true, out var made));
        Assert.NotNull(made);
        Assert.Equal((1, 1), (switches, sentinels));
    }

    [Fact] // [EDGE-sun-off-16]
    public void TickAll_ControllerLightingNightSunOff()
    {
        var order = new List<string>();
        Plugin.TickAll(() => order.Add("controller"), () => order.Add("lighting"), () => order.Add("night"), () => order.Add("sun"));
        Assert.Equal(["controller", "lighting", "night", "sun"], order);
    }

    [Fact] // [AC-sun-off-2] [EDGE-sun-off-11]
    public void TeardownOrder_LightingNightSunOffController()
    {
        var order = new List<string>();
        Plugin.TeardownOrder(() => order.Add("lighting"), () => order.Add("night"), () => order.Add("controller"), () => order.Add("sun"));
        Assert.Equal(["lighting", "night", "sun", "controller"], order);
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
        order.Clear();
        Plugin.TickAll(() => order.Add("controller"), null, null);
        Assert.Equal(["controller"], order);
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
    [Fact] // [AC-per-backdrop-settings-2]
    public void SaveConfig_StoresWorkingCopyIntoSelectedEntryBeforePersist()
    {
        var cfg = new Configuration { UserImages = [@"C:\a.jpg"] };
        cfg.SelectBackdrop(@"C:\a.jpg");
        cfg.Brightness = 0.35f;
        float? persisted = null;
        Plugin.SaveConfig(cfg, x => persisted = x.Backdrops[@"C:\a.jpg"].Brightness);
        Assert.Equal(0.35f, persisted);
    }

    [Fact] // [AC-fast-bc7-8] [EDGE-fast-bc7-12]
    public void CreateBlockEncoder_KillSwitch()
    {
        var fast = Plugin.CreateBlockEncoder(true);
        Assert.IsType<Milieu.Imaging.OpaqueBc7>(fast);
        Assert.Equal("opaque-bc7-1", fast.CacheId);
        var reference = Plugin.CreateBlockEncoder(false);
        Assert.IsType<Milieu.Imaging.Bc7>(reference);
        Assert.Equal("bcnenc-fast", reference.CacheId);
        // The production wiring goes through the switch (a regression to a hard-coded encoder would bypass it).
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoPaths.Root(), "Milieu", "Plugin.cs"));
        Assert.Contains("new BackdropPreparer(cache, CreateBlockEncoder(FastConversionAvailable()),", src);
    }
}
