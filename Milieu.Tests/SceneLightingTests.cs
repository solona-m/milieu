using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Milieu.Backdrop;
using Milieu.Lighting;
using Milieu.Sync;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public sealed class SceneLightingTests : IDisposable
{
    private const int Settle = LightRigLayout.GposeSettleTicks;
    private static readonly DomeView View0 = new(0, false, 1, 2, 1);
    private const string ImageA = @"C:\pics\sky.hdr";
    private const string ImageB = @"C:\pics\dusk.exr";

    private readonly TempDir dir = new();
    private readonly FakeLightSpawner spawner = new();
    private readonly FakeGame game = new() { IsGPosing = true };
    private readonly Configuration config = new() { SceneLighting = true };
    private readonly FakeLog log = new();
    private FakeSentinel sentinel = new();
    private LitBackdrop? lit = new(ImageA, View0);
    private bool unloading;
    private DeliveryMode delivery = DeliveryMode.Local;
    private readonly List<string> loads = [];
    private Func<string, CancellationToken, Task<LightRig?>>? loader;
    private int runs;
    private SceneLighting s;

    public SceneLightingTests() => s = Make();

    public void Dispose() => dir.Dispose();

    /// <summary>A sun in the upper sky plus four regions, all drawn at default repeats.</summary>
    internal static readonly LightRig RigA = new(LightExtractor.Version, new RigLight(0.6f, 0.3f, new Vector3(3, 2.5f, 2)),
        [new(0.3f, 0.3f, new Vector3(0.5f, 0.6f, 1f)), new(0.7f, 0.35f, new Vector3(0.4f, 0.5f, 0.9f)),
         new(0.4f, 0.7f, new Vector3(0.3f, 0.2f, 0.1f)), new(0.8f, 0.75f, new Vector3(0.2f, 0.2f, 0.1f))],
        new Vector3(10, 10, 10));

    internal static readonly LightRig RigB = RigA with { Key = new RigLight(0.2f, 0.4f, new Vector3(1, 1, 3)) };

    private SceneLighting Make(Func<Func<Task<LightRig?>>, Task<LightRig?>>? run = null) =>
        new(spawner, game, () => lit, config, Load, sentinel, log, () => unloading, () => delivery,
            run ?? (f => { runs++; return f(); }));

    private Task<LightRig?> Load(string path, CancellationToken ct)
    {
        loads.Add(path);
        if (loader != null) return loader(path, ct);
        return Task.FromResult<LightRig?>(path == ImageB ? RigB : RigA);
    }

    private void Ticks(int n) { for (var i = 0; i < n; i++) s.Tick(); }

    /// <summary>Settle, then the tick that creates the lights.</summary>
    private void LightUp()
    {
        Ticks(Settle + 1);
        Assert.Equal(3, spawner.Live.Count());
    }

    private Vector3 Anchor => game.PlayerPosition!.Value + new Vector3(0, LightRigLayout.AnchorHeight, 0);

    // ---------------------------------------------------------------- AC

    [Theory] // [AC-scene-lighting-1]
    [InlineData(ImageA)]
    [InlineData(ImageB)]
    public void GposeHdrShown_CreatesKeyAndFills_AfterSettle(string image)
    {
        lit = new LitBackdrop(image, View0);
        Ticks(Settle);
        Assert.Empty(spawner.Creates);
        s.Tick();
        var expected = LightRigPlanner.Plan(image == ImageB ? RigB : RigA, View0, Anchor, 1f, 2).Lights.Select(l => l.P).ToList();
        Assert.Equal(expected, spawner.Creates.Select(c => c.P));
        Assert.Equal(3, spawner.Creates.Count);
        Assert.Equal(LightShapeKind.Spot, spawner.Creates[0].P.Shape);
        Assert.Equal(LightingStatus.OnWithKey, s.Status.Status);
        Assert.Equal(2, s.Status.Fills);
        Assert.Equal(Path.GetFileName(image), s.Status.FileName);
        Assert.Equal([image], loads);
        Assert.Contains(log.Lines, l => l.Level == "Information" && l.Text.Contains("key at azimuth") && !l.Text.Contains(@"C:\"));
    }

    public static TheoryData<string> Removers => new()
    {
        "OptionOff", "LitNull", "GposeOff", "NotReady", "BetweenAreas", "Cutscene", "LoggedOut", "NoPlayer", "RemoveAll", "Teardown",
    };

    [Theory] // [AC-scene-lighting-2] [EDGE-scene-lighting-20]
    [MemberData(nameof(Removers))]
    public void RemovedSameTick_On(string what)
    {
        LightUp();
        var held = spawner.Live.ToList();
        switch (what)
        {
            case "OptionOff": config.SceneLighting = false; s.Tick(); break;
            case "LitNull": lit = null; s.Tick(); break;
            case "GposeOff": game.IsGPosing = false; s.Tick(); break;
            case "NotReady": game.Ready = false; s.Tick(); break;
            case "BetweenAreas": game.BetweenAreas = true; s.Tick(); break;
            case "Cutscene": game.InCutscene = true; s.Tick(); break;
            case "LoggedOut": game.IsLoggedIn = false; s.Tick(); break;
            case "NoPlayer": game.PlayerPosition = null; s.Tick(); break;
            case "RemoveAll": s.RemoveAll(); break;
            case "Teardown": s.Teardown(); break;
        }
        Assert.Empty(spawner.Live);
        Assert.Equal(held, spawner.Destroys);
        Assert.Equal(0, s.LiveCount);
    }

    [Fact] // [AC-scene-lighting-2] [EDGE-scene-lighting-21]
    public void HiddenEvent_RemovesWithinCall()
    {
        var vfx = new FakeSpawner();
        var pen = new FakePenumbra();
        var cache = new CacheStore(dir.File("cache"));
        var prep = new FakePreparer(cache);
        config.ImagePath = dir.File("sky.hdr");
        var c = new BackdropController(vfx, pen, game, prep, cache, config, log, run: f => f());
        s = new SceneLighting(spawner, game, () => c.Lit, config, Load, sentinel, log, () => unloading, () => delivery, f => f());
        c.Hidden += s.RemoveAll;
        c.Show(); c.Tick(); c.Tick();
        Assert.Equal(BackdropState.Shown, c.State);
        LightUp();

        c.Hide();                                            // the None tile and /milieu off: no tick in between
        Assert.Empty(spawner.Live);
        Assert.Equal(3, spawner.Destroys.Count);
        s.Tick();
        Assert.Equal(LightingStatus.WaitingForBackdrop, s.Status.Status);
        Assert.Equal(3, spawner.Creates.Count);
    }

    [Fact] // [AC-scene-lighting-2] [AC-scene-lighting-3] [AC-scene-lighting-9]
    public void DefaultConfig_LightsHdrInGposeOnly()
    {
        var fresh = new Configuration();                     // on by default (owner change)
        Assert.True(fresh.SceneLighting);
        s = new SceneLighting(spawner, game, () => lit, fresh, Load, sentinel, log, () => unloading, () => delivery, f => f());
        lit = new LitBackdrop(@"C:\pics\sky.png", View0);
        Ticks(Settle + 2);
        Assert.Equal(0, spawner.NativeCalls);                // LDR: the note, no lights
        Assert.Equal(LightingStatus.NeedsHdr, s.Status.Status);
        lit = new LitBackdrop(ImageA, View0);
        game.IsGPosing = false;
        Ticks(Settle + 2);
        Assert.Equal(0, spawner.NativeCalls);                // HDR outside GPose: none
        game.IsGPosing = true;
        Ticks(Settle + 1);
        Assert.Equal(3, spawner.Live.Count());               // HDR in GPose: lit with no user action
        // The crash sentinel still latches a default-on config off.
        var guarded = new SceneLighting(new FakeLightSpawner(), game, () => lit, fresh, Load, new FakeSentinel(setAtStartup: true), log,
                                        () => false, () => delivery, f => f());
        for (var i = 0; i < Settle + 2; i++) guarded.Tick();
        Assert.Equal(LightingStatus.CrashGuard, guarded.Status.Status);
    }

    [Theory] // [AC-scene-lighting-3]
    [InlineData(@"C:\pics\sky.png")]
    [InlineData(@"C:\pics\sky.jpg")]
    [InlineData("bundled:moon")]
    public void LdrLit_NoCreateNoJob(string image)
    {
        lit = new LitBackdrop(image, View0);
        Ticks(Settle + 5);
        Assert.Equal(0, spawner.NativeCalls);
        Assert.Empty(loads);
        Assert.Equal(LightingStatus.NeedsHdr, s.Status.Status);
    }

    [Fact] // [AC-scene-lighting-4]
    public void StrengthOrFillCountChange_UpdatesInPlace()
    {
        LightUp();
        var created = spawner.Creates.Count;
        config.LightStrength = 0.5f;
        s.Tick();
        Assert.Equal(created, spawner.Creates.Count);
        Assert.Equal(3, spawner.Updates.Count);
        Assert.Empty(spawner.Destroys);
        Assert.Equal(LightRigPlanner.Plan(RigA, View0, Anchor, 0.5f, 2).Lights[0].P.Colour, spawner.Updates[0].P.Colour);

        config.FillLightCount = 3;
        s.Tick();
        Assert.Equal(created + 1, spawner.Creates.Count);
        Assert.Equal(4, spawner.Live.Count());

        config.FillLightCount = 1;
        s.Tick();
        Assert.Equal(2, spawner.Destroys.Count);
        Assert.Equal(2, spawner.Live.Count());
        Assert.Equal(1, s.Status.Fills);
    }

    [Fact] // [AC-scene-lighting-4]
    public void LitViewChange_ReaimsWithoutRecreate()
    {
        LightUp();
        lit = new LitBackdrop(ImageA, View0 with { YawRadians = MathF.PI / 2, Flip = true, URepeat = 1.5f });
        s.Tick();
        Assert.Equal(3, spawner.Creates.Count);
        Assert.Empty(spawner.Destroys);
        var expected = LightRigPlanner.Plan(RigA, lit.View, Anchor, 1f, 2).Lights.Select(l => l.P).ToList();
        Assert.Equal(expected, spawner.Updates.Select(u => u.P));
        Assert.Single(loads);                                 // same image: no new rig job
    }

    [Fact] // [AC-scene-lighting-4]
    public void ConfigEditWithoutRedraw_DoesNotReaim()
    {
        var vfx = new FakeSpawner();
        var cache = new CacheStore(dir.File("cache"));
        config.ImagePath = dir.File("sky.hdr");
        var c = new BackdropController(vfx, new FakePenumbra(), game, new FakePreparer(cache), cache, config, log, run: f => f());
        s = new SceneLighting(spawner, game, () => c.Lit, config, Load, sentinel, log, () => unloading, () => delivery, f => f());
        c.Show(); c.Tick(); c.Tick();
        LightUp();

        config.Sky.YawDegrees = 90;                          // saved, not yet applied: the dome hasn't moved
        config.HorizontalRepeat = 3;
        Ticks(3);
        Assert.Empty(spawner.Updates);

        c.ApplyPlacement();                                  // now it is redrawn at the new yaw
        s.Tick();
        Assert.Equal(3, spawner.Updates.Count);
        Assert.Equal(3, spawner.Creates.Count);
    }

    [Fact] // [AC-scene-lighting-6] [EDGE-scene-lighting-22]
    public void SyncedLit_CreatesLocally()
    {
        delivery = DeliveryMode.MadBirdRing;
        lit = new LitBackdrop(ImageA, View0 with { YawRadians = 1.2f });
        LightUp();
        Assert.Equal(LightRigPlanner.Plan(RigA, lit.View, Anchor, 1f, 2).Lights.Select(l => l.P), spawner.Creates.Select(c => c.P));
        lit = null;                                          // the synced registration removed
        s.Tick();
        Assert.Empty(spawner.Live);
    }

    [Fact] // [AC-scene-lighting-7] [EDGE-scene-lighting-15]
    public void CreateFailsMidway_DestroysPartial_LatchesNoRetry()
    {
        spawner.FailCreateAt = 2;
        Ticks(Settle + 1);
        Assert.Single(spawner.Creates);
        Assert.Equal(spawner.Creates[0].H, Assert.Single(spawner.Destroys));
        Assert.Equal(LightingStatus.CreateFailed, s.Status.Status);
        Assert.Contains(log.Lines, l => l.Level == "Warning" && l.Text.Contains("didn't create"));
        Ticks(Settle * 3);
        Assert.Equal(2, spawner.CreateAttempts);              // latched: no retry loop

        config.SceneLighting = false; s.Tick();
        config.SceneLighting = true;
        Ticks(Settle + 1);                                   // off → on retries, after the settle delay
        Assert.Equal(3, spawner.Live.Count());
        Assert.Equal(LightingStatus.OnWithKey, s.Status.Status);
    }

    [Fact] // [AC-scene-lighting-7] [EDGE-scene-lighting-16]
    public void SpawnerUnavailable_NoNativeCalls_StatusUnavailable()
    {
        spawner.Available = false;
        Ticks(Settle * 2);
        s.RemoveAll();
        s.Teardown();
        Assert.Equal(0, spawner.NativeCalls);
        Assert.Empty(loads);
        Assert.Equal(LightingStatus.Unavailable, s.Status.Status);
    }

    [Fact] // [AC-scene-lighting-7] [EDGE-scene-lighting-33]
    public void SentinelAtStartup_LatchedCrashGuard_NoCreate()
    {
        sentinel = new FakeSentinel(setAtStartup: true);
        s = Make();
        Ticks(Settle * 2);
        Assert.Equal(0, spawner.NativeCalls);
        Assert.Equal(LightingStatus.CrashGuard, s.Status.Status);
        Assert.True(sentinel.IsSet);                          // stays until the user retries
        Assert.Contains(log.Lines, l => l.Level == "Warning" && l.Text.Contains("closed"));
    }

    [Fact] // [AC-scene-lighting-8]
    public void JobRunsViaRun_NotInline()
    {
        Func<Task<LightRig?>>? captured = null;
        var tcs = new TaskCompletionSource<LightRig?>();
        s = Make(f => { captured = f; return tcs.Task; });
        s.Tick();
        Assert.NotNull(captured);
        Assert.Empty(loads);                                  // not called on the framework thread
        Assert.Equal(LightingStatus.Analysing, s.Status.Status);
        Ticks(5);
        Assert.Empty(loads);
        tcs.SetResult(RigA);
        Ticks(Settle + 1);
        Assert.Equal(3, spawner.Live.Count());
    }

    [Fact] // [AC-scene-lighting-8]
    public void ToggleOnWhileShown_AnalysingThenOn()
    {
        config.SceneLighting = false;
        Ticks(3);
        Assert.Equal(LightingStatus.Off, s.Status.Status);
        Assert.Empty(loads);
        var tcs = new TaskCompletionSource<LightRig?>();
        loader = (_, _) => tcs.Task;
        config.SceneLighting = true;
        s.Tick();
        Assert.Equal(LightingStatus.Analysing, s.Status.Status);
        Assert.Equal("sky.hdr", s.Status.FileName);
        Ticks(3);
        Assert.Equal(LightingStatus.Analysing, s.Status.Status);
        tcs.SetResult(RigA);
        s.Tick();
        Assert.Equal(LightingStatus.WaitingForGPose, s.Status.Status);   // the settle delay
        Ticks(Settle);
        Assert.Equal(LightingStatus.OnWithKey, s.Status.Status);
        Assert.Single(loads);
    }

    [Fact] // [AC-scene-lighting-9]
    public void NotInGpose_NoLights_StatusWaitingForGpose()
    {
        game.IsGPosing = false;
        Ticks(Settle * 3);
        Assert.Equal(0, spawner.NativeCalls);
        Assert.Empty(loads);
        Assert.Equal(LightingStatus.WaitingForGPose, s.Status.Status);
    }

    // ---------------------------------------------------------------- edges

    [Fact] // [EDGE-scene-lighting-4]
    public void BlackRig_EmptyPlan_OnNoKeyZeroFills()
    {
        loader = (_, _) => Task.FromResult<LightRig?>(new LightRig(LightExtractor.Version, null,
            Enumerable.Repeat(new RigLight(0.5f, 0.5f, Vector3.Zero), 4).ToArray(), Vector3.Zero));
        Ticks(Settle + 3);
        Assert.Empty(spawner.Creates);
        Assert.Equal(new LightingStatusSnapshot(LightingStatus.OnNoKey, "sky.hdr", 0), s.Status);
        Assert.Equal(0, sentinel.Sets);                       // nothing to create, nothing to guard
    }

    [Fact] // [EDGE-scene-lighting-10] [EDGE-scene-lighting-38]
    public void StrengthZero_NoLights_StatusStrengthZero()
    {
        config.LightStrength = 0;
        Ticks(Settle + 2);
        Assert.Empty(spawner.Creates);
        Assert.Equal(LightingStatus.StrengthZero, s.Status.Status);
    }

    [Fact] // [EDGE-scene-lighting-12]
    public void ImageChange_OldLightsUntilNewCommit_ThenNewRig()
    {
        LightUp();
        var held = spawner.Live.ToList();
        // B converting: the controller still draws A, so Lit (and the lights) stay A.
        Ticks(10);
        Assert.Equal(held, spawner.Live);
        Assert.Empty(spawner.Updates);

        var tcs = new TaskCompletionSource<LightRig?>();
        loader = (p, _) => p == ImageB ? tcs.Task : Task.FromResult<LightRig?>(RigA);
        lit = new LitBackdrop(ImageB, View0);                // B committed
        var refreshes = spawner.Refreshes.Count;
        s.Tick();
        Assert.Equal(LightingStatus.Analysing, s.Status.Status);
        Assert.Equal(held, spawner.Live);                    // A's lights until B's rig is in
        Ticks(4);
        Assert.Equal(refreshes + 5 * 3, spawner.Refreshes.Count);   // still refreshed every tick while analysing
        tcs.SetResult(RigB);
        s.Tick();
        Assert.Equal(held, spawner.Live);                    // re-aimed in place, not recreated
        // RigB differs from RigA only in its key: just the key light is rewritten.
        var update = Assert.Single(spawner.Updates);
        Assert.Equal(held[0], update.H);
        Assert.Equal(LightRigPlanner.Plan(RigB, View0, Anchor, 1f, 2).Lights[0].P, update.P);
        Assert.Equal([ImageA, ImageB], loads);
    }

    [Fact] // [EDGE-scene-lighting-12]
    public void ImageChangeToLdr_RemovesOnCommit()
    {
        LightUp();
        lit = new LitBackdrop(@"C:\pics\next.png", View0);
        s.Tick();
        Assert.Empty(spawner.Live);
        Assert.Equal(LightingStatus.NeedsHdr, s.Status.Status);
        Assert.Single(loads);
    }

    [Fact] // [EDGE-scene-lighting-13]
    public void StaleJobResult_Ignored()
    {
        var tokens = new List<CancellationToken>();
        var gates = new Dictionary<string, TaskCompletionSource<LightRig?>>();
        loader = (p, ct) =>
        {
            tokens.Add(ct);
            var g = new TaskCompletionSource<LightRig?>();
            gates[p] = g;
            return g.Task;
        };
        s.Tick();                                            // job for A
        lit = new LitBackdrop(ImageB, View0);
        s.Tick();                                            // path changed: A's job cancelled, B's started
        Assert.True(tokens[0].IsCancellationRequested);
        gates[ImageA].SetResult(RigA);
        Ticks(Settle + 2);
        Assert.Empty(spawner.Creates);                       // A's late result never used
        Assert.Equal(LightingStatus.Analysing, s.Status.Status);

        // Option off while B's job runs: cancelled; its result is ignored, and a new job starts once back on.
        config.SceneLighting = false;
        s.Tick();
        Assert.True(tokens[1].IsCancellationRequested);
        gates[ImageB].SetResult(RigA);                       // wrong rig, late: must not be adopted
        config.SceneLighting = true;
        s.Tick();
        Assert.Equal(3, loads.Count);
        gates[ImageB].SetResult(RigB);
        Ticks(Settle + 1);
        Assert.Equal(LightRigPlanner.Plan(RigB, View0, Anchor, 1f, 2).Lights.Select(l => l.P), spawner.Creates.Select(c => c.P));

        // GPose ending mid-job: cancelled too.
        game.IsGPosing = false;
        lit = new LitBackdrop(ImageA, View0);
        s.Tick();
        game.IsGPosing = true;
        s.Tick();                                            // a job for A
        game.IsGPosing = false;
        s.Tick();
        Assert.True(tokens[^1].IsCancellationRequested);
    }

    [Fact] // [EDGE-scene-lighting-14]
    public void JobFaults_ReadFailedLatched_NoRepeatJobs()
    {
        loader = (_, _) => Task.FromException<LightRig?>(new MilieuException(StatusMessage.Of(MessageKey.DecodeFailed, ("file", "sky.hdr"))));
        Ticks(Settle * 2);
        Assert.Single(loads);
        Assert.Equal(new LightingStatusSnapshot(LightingStatus.ReadFailed, "sky.hdr"), s.Status);
        Assert.Equal(0, spawner.NativeCalls);

        // Another image clears ReadFailed.
        loader = null;
        lit = new LitBackdrop(ImageB, View0);
        Ticks(Settle + 1);
        Assert.Equal(2, loads.Count);
        Assert.Equal(LightingStatus.OnWithKey, s.Status.Status);

        // Off → on clears it too.
        loader = (_, _) => Task.FromException<LightRig?>(new InvalidDataException("boom"));
        lit = new LitBackdrop(@"C:\pics\third.hdr", View0);
        Ticks(3);
        Assert.Equal(LightingStatus.ReadFailed, s.Status.Status);
        Assert.Empty(spawner.Live);
        Assert.Contains(log.Lines, l => l.Level == "Warning" && l.Text.Contains("Reading the light failed"));
        loader = null;
        config.SceneLighting = false; s.Tick();
        config.SceneLighting = true;
        Ticks(Settle + 1);
        Assert.Equal(LightingStatus.OnWithKey, s.Status.Status);
    }

    [Fact] // [EDGE-scene-lighting-17]
    public void DeadHandleOnRefresh_NoCallOnIt_OthersDestroyed_Latched()
    {
        LightUp();
        var gone = spawner.Creates[1].H;
        spawner.GoneByGame.Add(gone);
        s.Tick();                                            // Refresh finds it dead
        Assert.True(gone.IsDead);
        Assert.DoesNotContain(gone, spawner.Destroys);
        Assert.Equal(2, spawner.Destroys.Count);
        Assert.Empty(spawner.Live);
        Assert.Equal(LightingStatus.CreateFailed, s.Status.Status);
        var calls = spawner.NativeCalls;
        Ticks(Settle * 2);
        Assert.Equal(calls, spawner.NativeCalls);

        // A dead handle found by Update (a re-aim) behaves the same.
        config.SceneLighting = false; s.Tick();
        config.SceneLighting = true;
        LightUp();
        var gone2 = spawner.Creates[^3].H;
        spawner.GoneByGame.Add(gone2);
        config.LightStrength = 1.5f;
        s.Tick();
        Assert.DoesNotContain(gone2, spawner.Destroys);
        Assert.Empty(spawner.Live);
        Assert.Equal(LightingStatus.CreateFailed, s.Status.Status);
    }

    [Fact] // [EDGE-scene-lighting-18]
    public void DestroyRefused_HandleKeptRetriedNextTick()
    {
        LightUp();
        spawner.RefuseDestroy = true;
        config.SceneLighting = false;
        s.Tick();
        Assert.Equal(3, s.LiveCount);                         // kept, never orphaned
        Assert.Empty(spawner.Destroys);
        spawner.RefuseDestroy = false;
        s.Tick();
        Assert.Equal(0, s.LiveCount);
        Assert.Equal(3, spawner.Destroys.Count);
    }

    [Fact] // [EDGE-scene-lighting-19]
    public void GposeExitThenReenter_RecreatesFromMemory_NoSecondJob_AfterSettle()
    {
        LightUp();
        for (var round = 0; round < 3; round++)
        {
            game.IsGPosing = false;
            s.Tick();
            Assert.Empty(spawner.Live);
            game.IsGPosing = true;
            Ticks(Settle);
            Assert.Empty(spawner.Live);
            s.Tick();
            Assert.Equal(3, spawner.Live.Count());
        }
        Assert.Single(loads);
        Assert.Equal(1, runs);
    }

    [Fact] // [EDGE-scene-lighting-21]
    public void LitNullSameTick_Removed()
    {
        LightUp();
        lit = null;
        s.Tick();
        Assert.Empty(spawner.Live);
        Assert.Equal(LightingStatus.WaitingForBackdrop, s.Status.Status);
    }

    [Fact] // [EDGE-scene-lighting-23]
    public void CancelJobForDispose_CancelsAndReturnsTask()
    {
        CancellationToken token = default;
        var tcs = new TaskCompletionSource<LightRig?>();
        loader = (_, ct) => { token = ct; return tcs.Task; };
        Assert.Null(s.CancelJobForDispose());
        s.Tick();
        var running = s.CancelJobForDispose();
        Assert.NotNull(running);
        Assert.True(token.IsCancellationRequested);
        Assert.False(running!.IsCompleted);
        tcs.SetCanceled();
        Assert.True(BackdropController.WaitForJob(running, TimeSpan.FromSeconds(1)));
    }

    [Fact] // [EDGE-scene-lighting-23]
    public void TeardownDestroysAll()
    {
        LightUp();
        s.Teardown();
        Assert.Empty(spawner.Live);
        Assert.Equal(3, spawner.Destroys.Count);
        unloading = true;                                    // after unhook nothing calls in; even a stray tick is inert
        s.Tick();
        s.Teardown();
        Assert.Equal(3, spawner.Destroys.Count);
    }

    [Fact] // [EDGE-scene-lighting-29]
    public void AnchorFollowsPlayer_Threshold()
    {
        LightUp();
        game.PlayerPosition += new Vector3(0.03f, 0, 0);
        s.Tick();
        Assert.Empty(spawner.Updates);                        // under 0.05: not moved
        game.PlayerPosition += new Vector3(0.03f, 0, 0);
        s.Tick();
        Assert.Equal(3, spawner.Updates.Count);
        var expected = LightRigPlanner.Plan(RigA, View0, Anchor, 1f, 2).Lights.Select(l => l.P);
        Assert.Equal(expected, spawner.Updates.Select(u => u.P));
        s.Tick();
        Assert.Equal(3, spawner.Updates.Count);
    }

    [Fact] // [EDGE-scene-lighting-32]
    public void Unloading_TickRemoveAllTeardown_NoNativeCalls_HandlesDropped_SentinelCleared()
    {
        LightUp();
        Assert.True(sentinel.IsSet);
        var calls = spawner.NativeCalls;
        unloading = true;
        s.Tick();
        Assert.Equal(calls, spawner.NativeCalls);
        s.RemoveAll();
        s.Teardown();
        Assert.Equal(calls, spawner.NativeCalls);             // no Destroy, no Refresh
        Assert.Equal(0, s.LiveCount);                         // dropped on purpose
        Assert.False(sentinel.IsSet);                         // a game exit is not a crash
        Assert.Contains(log.Lines, l => l.Level == "Debug" && l.Text.Contains("Game exiting"));
    }

    [Fact] // [EDGE-scene-lighting-35]
    public void GposeSettle_NoCreateBeforeTicks()
    {
        for (var i = 0; i < Settle; i++)
        {
            s.Tick();
            Assert.Empty(spawner.Creates);
            Assert.Equal(LightingStatus.WaitingForGPose, s.Status.Status);
        }
        s.Tick();
        Assert.Equal(3, spawner.Creates.Count);
    }

    public static TheoryData<string> SettleResets => new() { "GposeOff", "LitNull", "OptionOff", "NotReady" };

    [Theory] // [EDGE-scene-lighting-35]
    [MemberData(nameof(SettleResets))]
    public void GposeSettle_ResetsWheneverShouldLightFalse(string what)
    {
        Ticks(Settle - 1);
        void Set(bool off)
        {
            switch (what)
            {
                case "GposeOff": game.IsGPosing = !off; break;
                case "LitNull": lit = off ? null : new LitBackdrop(ImageA, View0); break;
                case "OptionOff": config.SceneLighting = !off; break;
                case "NotReady": game.Ready = !off; break;
            }
        }
        Set(true);
        s.Tick();                                            // one tick without ShouldLight: the counter is back to 0
        Set(false);
        Ticks(Settle);
        Assert.Empty(spawner.Creates);
        s.Tick();
        Assert.Equal(3, spawner.Creates.Count);
    }

    [Fact] // [EDGE-scene-lighting-33]
    public void CrashGuard_ToggleOffOn_ClearsLatchAndFile()
    {
        var path = dir.File(FileLightingSentinel.FileName);
        File.WriteAllBytes(path, []);
        var file = new FileLightingSentinel(path, log);
        s = new SceneLighting(spawner, game, () => lit, config, Load, file, log, () => unloading, () => delivery, f => f());
        Ticks(Settle * 2);
        Assert.Equal(LightingStatus.CrashGuard, s.Status.Status);
        Assert.True(File.Exists(path));
        config.SceneLighting = false; s.Tick();
        Assert.True(File.Exists(path));                       // off alone isn't the retry
        config.SceneLighting = true; s.Tick();
        Assert.False(File.Exists(path));
        Assert.NotEqual(LightingStatus.CrashGuard, s.Status.Status);
        Ticks(Settle);
        Assert.Equal(3, spawner.Live.Count());
        Assert.True(File.Exists(path));                       // this attempt is guarded in turn
    }

    [Fact] // [EDGE-scene-lighting-34]
    public void SentinelSetBeforeEachBatch_ClearedAfterStableTicks()
    {
        spawner.RequireSentinelSet = sentinel;               // every Create throws unless the sentinel is already set
        Ticks(Settle);
        Assert.Equal(0, sentinel.Sets);
        s.Tick();
        Assert.Equal(1, sentinel.Sets);
        Assert.Equal("Set", sentinel.Calls[^1]);
        Assert.Equal(3, spawner.Creates.Count);              // all three created after the Set
        Ticks(LightRigLayout.StableTicks - 2);
        Assert.True(sentinel.IsSet);                          // not yet: the window is StableTicks with lights alive
        s.Tick();
        Assert.False(sentinel.IsSet);
        var clears = sentinel.Clears;
        Ticks(50);
        Assert.Equal(clears, sentinel.Clears);

        // A new batch (GPose re-entered) is guarded again.
        game.IsGPosing = false; s.Tick();
        game.IsGPosing = true;
        Ticks(Settle + 1);
        Assert.Equal(2, sentinel.Sets);
        Assert.True(sentinel.IsSet);
        // Adding a light to a live set is not a new batch.
        config.FillLightCount = 3;
        s.Tick();
        Assert.Equal(2, sentinel.Sets);
    }

    [Fact] // [EDGE-scene-lighting-34]
    public void SentinelClearedOnTeardown()
    {
        LightUp();
        Assert.True(sentinel.IsSet);
        s.Teardown();
        Assert.False(sentinel.IsSet);
    }

    [Fact] // [EDGE-scene-lighting-34]
    public void SentinelNotClearedBeforeStable_NewInstanceSeesIt()
    {
        var path = dir.File(FileLightingSentinel.FileName);
        var file = new FileLightingSentinel(path, log);
        s = new SceneLighting(spawner, game, () => lit, config, Load, file, log, () => unloading, () => delivery, f => f());
        LightUp();
        Ticks(LightRigLayout.StableTicks / 2);
        // The game "crashes" here: the next session's sentinel sees the attempt.
        var next = new FileLightingSentinel(path, log);
        Assert.True(next.WasSetAtStartup);
        var after = new SceneLighting(new FakeLightSpawner(), game, () => lit, config, Load, next, log, () => false, () => delivery, f => f());
        after.Tick();
        Assert.Equal(LightingStatus.CrashGuard, after.Status.Status);
    }

    [Fact] // [EDGE-scene-lighting-42]
    public void RemoveAll_ClearsSentinelOnlyWhenAllDestroyed()
    {
        LightUp();
        Assert.True(sentinel.IsSet);
        spawner.RefuseDestroy = true;
        s.RemoveAll();
        Assert.True(sentinel.IsSet);                          // one (here: every) Destroy refused
        Assert.Equal(3, s.LiveCount);
        spawner.RefuseDestroy = false;
        s.RemoveAll();
        Assert.Equal(0, s.LiveCount);
        Assert.False(sentinel.IsSet);
        // With no lights there's nothing to clear: a CrashGuard sentinel is never wiped by a RemoveAll.
        var guarded = new FakeSentinel(setAtStartup: true);
        var g = new SceneLighting(spawner, game, () => lit, config, Load, guarded, log, () => false, () => delivery, f => f());
        g.RemoveAll();
        g.Tick();
        Assert.True(guarded.IsSet);
    }

    [Fact] // [EDGE-scene-lighting-42]
    public void RemoveAll_OneOfThreeRefused_SentinelStays()
    {
        var refusing = new RefusingSpawner(spawner);
        var t = new SceneLighting(refusing, game, () => lit, config, Load, sentinel, log, () => false, () => delivery, f => f());
        for (var i = 0; i < Settle + 1; i++) t.Tick();
        Assert.Equal(3, t.LiveCount);
        Assert.True(sentinel.IsSet);
        refusing.Active = true;
        t.RemoveAll();
        Assert.Equal(1, t.LiveCount);                         // the refused one is kept for a retry
        Assert.True(sentinel.IsSet);
        refusing.Active = false;
        t.RemoveAll();
        Assert.Equal(0, t.LiveCount);
        Assert.False(sentinel.IsSet);
    }

    /// <summary>Wraps a fake spawner and, while active, refuses Destroy for the third light it created.</summary>
    private sealed class RefusingSpawner(FakeLightSpawner inner) : ILightSpawner
    {
        public bool Active;
        private int creates;
        private LightHandle? third;
        public bool Available => inner.Available;
        public LightHandle? Create(LightParams p)
        {
            var h = inner.Create(p);
            if (++creates == 3) third = h;
            return h;
        }
        public bool Update(LightHandle h, LightParams p) => inner.Update(h, p);
        public bool Refresh(LightHandle h) => inner.Refresh(h);
        public bool Destroy(LightHandle h) => !(Active && ReferenceEquals(h, third)) && inner.Destroy(h);
    }

    [Fact] // [EDGE-scene-lighting-17]
    public void DeadHandleWhileAnalysing_FailsAndRemoves()
    {
        LightUp();
        loader = (_, _) => new TaskCompletionSource<LightRig?>().Task;   // B's rig never arrives
        lit = new LitBackdrop(ImageB, View0);
        s.Tick();
        Assert.Equal(LightingStatus.Analysing, s.Status.Status);
        var gone = spawner.Creates[0].H;
        spawner.GoneByGame.Add(gone);
        s.Tick();
        Assert.DoesNotContain(gone, spawner.Destroys);
        Assert.Empty(spawner.Live);
        Assert.Equal(LightingStatus.CreateFailed, s.Status.Status);
    }

    [Fact] // [EDGE-scene-lighting-34]
    public void StableCountContinuesWhileAnalysing()
    {
        LightUp();
        Ticks(LightRigLayout.StableTicks - 100);
        loader = (_, _) => new TaskCompletionSource<LightRig?>().Task;
        lit = new LitBackdrop(ImageB, View0);
        Ticks(100);
        Assert.Equal(LightingStatus.Analysing, s.Status.Status);
        Assert.False(sentinel.IsSet);                         // the window kept counting with A's lights alive
    }

    public static TheoryData<string> EmptyPlans => new() { "StrengthZero", "NoKeyNoFills", "NothingDrawn" };

    [Theory] // [EDGE-scene-lighting-34] [EDGE-scene-lighting-10]
    [MemberData(nameof(EmptyPlans))]
    public void ApplyLeavingNoLights_ClearsOwnedSentinel(string what)
    {
        LightUp();
        Assert.True(sentinel.IsSet);
        switch (what)
        {
            case "StrengthZero": config.LightStrength = 0; break;
            case "NoKeyNoFills": loader = (_, _) => Task.FromResult<LightRig?>(RigA with { Key = null }); config.FillLightCount = 0;
                                 lit = new LitBackdrop(ImageB, View0); break;
            case "NothingDrawn": lit = new LitBackdrop(ImageA, View0 with { URepeat = 0.01f, VRepeat = 0.01f }); break;
        }
        s.Tick();
        Assert.Empty(spawner.Live);
        Assert.False(sentinel.IsSet);
        // A refused destroy keeps the light, and the sentinel with it.
        config.LightStrength = 1; config.FillLightCount = 2; loader = null; lit = new LitBackdrop(ImageA, View0);
        s.Tick();
        Assert.True(sentinel.IsSet);
        spawner.RefuseDestroy = true;
        config.LightStrength = 0;
        s.Tick();
        Assert.True(sentinel.IsSet);
    }
}
