using Milieu.Backdrop;
using Milieu.Gui;
using Milieu.Lighting;
using Milieu.Sync;
using Xunit;

namespace Milieu.Tests;

public class LightingViewModelTests
{
    private const string Hdr = @"C:\Users\someone\Pictures\Panoramas\sky.hdr";

    private static LightingViewModel Vm(LightingStatus status, string file = "sky.hdr", int fills = 2, string? image = Hdr, bool on = true,
                                        bool available = true, DeliveryMode delivery = DeliveryMode.Local, bool estimated = false,
                                        bool localLights = false) =>
        new(new LightingStatusSnapshot(status, file, fills, estimated), new Configuration { ImagePath = image, SceneLighting = on }, available, delivery,
            localLights);

    private const string OldHdrNote = "Only .hdr and .exr images carry the light information this needs.";

    [Theory] // [AC-scene-lighting-3] [AC-ldr-light-estimate-5]
    [InlineData(@"C:\pics\sky.png")]
    [InlineData(@"C:\pics\sky.jpg")]
    [InlineData(@"C:\pics\sky.jpeg")]
    [InlineData("bundled:moon")]
    public void LdrOrBundledSelected_NoNeedsHdrText(string image)
    {
        // AC-scene-lighting-3 is superseded by docs/adr/0010: JPG/PNG and bundled scenes get estimated lights.
        foreach (var status in new[] { LightingStatus.WaitingForGPose, LightingStatus.Analysing, LightingStatus.OnWithKey, LightingStatus.OnNoKey })
        {
            var vm = Vm(status, image: image, estimated: true);
            Assert.Equal(status, vm.ShownStatus);
            Assert.DoesNotContain(OldHdrNote, vm.StatusText);
            Assert.DoesNotContain(OldHdrNote, vm.Notes);
        }
        Assert.Equal(LightingStatus.Off, Vm(LightingStatus.Off, image: image, on: false).ShownStatus);
        Assert.Equal("", Vm(LightingStatus.Off, image: image, on: false).StatusText);
    }

    [Fact] // [AC-ldr-light-estimate-5]
    public void Estimated_OnStatesOnly()
    {
        const string jpg = @"C:\pics\room.jpg";
        Assert.Equal("Estimated from the image: main light + 2 fill lights.", Vm(LightingStatus.OnWithKey, image: jpg, estimated: true).StatusText);
        Assert.Equal("Estimated from the image: no bright spot found, 3 fill lights.",
                     Vm(LightingStatus.OnNoKey, fills: 3, image: jpg, estimated: true).StatusText);
        Assert.Equal("Estimated from the image: the main light isn't on the dome at these repeat settings, 1 fill lights.",
                     Vm(LightingStatus.OnKeyNotDrawn, fills: 1, image: jpg, estimated: true).StatusText);
        // Every other status keeps its text, estimated or not.
        foreach (var status in new[] { LightingStatus.WaitingForBackdrop, LightingStatus.WaitingForGPose, LightingStatus.Analysing,
                                       LightingStatus.CreateFailed, LightingStatus.ReadFailed, LightingStatus.StrengthZero, LightingStatus.CrashGuard })
            Assert.Equal(Vm(status, file: "room.jpg", image: jpg).StatusText, Vm(status, file: "room.jpg", image: jpg, estimated: true).StatusText);
        // HDR lights keep the "Lit by the image" texts.
        Assert.Equal("Lit by the image: sun + 2 fill lights.", Vm(LightingStatus.OnWithKey).StatusText);
    }

    [Fact] // [AC-ldr-light-estimate-5]
    public void WaitingForBackdrop_NewText()
    {
        Assert.Equal("Lights appear while a backdrop is shown.", Vm(LightingStatus.WaitingForBackdrop, image: null).StatusText);
        Assert.Equal(LightingViewModel.WaitingForBackdropText, Vm(LightingStatus.WaitingForBackdrop, image: @"C:\pics\a.png").StatusText);
    }

    [Fact] // [AC-ldr-light-estimate-3]
    public void EstimatedNote_DirectionAndSmallestLampSize()
    {
        const string note = "This image has no real light data, so the lights are estimated from its brightest spots at a fixed brightness. "
                            + "Each light sits in its spot's direction at a fixed distance, not where the lamp really is. "
                            + "Lamps smaller than about a quarter of a degree (about 6 pixels in an 8K image) are missed.";
        Assert.Equal(note, LightingViewModel.EstimatedNote);
        foreach (var image in new[] { @"C:\pics\a.png", @"C:\pics\a.JPG", "bundled:lodge" })
        {
            Assert.Equal([note, "Only you see these lights."], Vm(LightingStatus.OnWithKey, image: image, estimated: true).Notes);
            Assert.Equal([note, "Only you see these lights.", "PSync pairs see the backdrop but not these lights."],
                         Vm(LightingStatus.WaitingForGPose, image: image, delivery: DeliveryMode.Cushion).Notes);
        }
        Assert.DoesNotContain(note, Vm(LightingStatus.OnWithKey).Notes);   // an HDR selection: real light data
        Assert.Empty(Vm(LightingStatus.Unavailable, image: @"C:\pics\a.png").Notes);
    }

    [Fact] // [AC-ldr-light-estimate-4]
    public void EstimatedNoKey_Text()
    {
        Assert.Equal("Estimated from the image: no bright spot found, 2 fill lights.",
                     Vm(LightingStatus.OnNoKey, image: @"C:\pics\overcast.jpg", estimated: true).StatusText);
        Assert.Equal("Estimated from the image: no bright spot found, 0 fill lights.",
                     Vm(LightingStatus.OnNoKey, fills: 0, image: "bundled:forest", estimated: true).StatusText);
    }

    [Fact] // [EDGE-ldr-light-estimate-19]
    public void StrengthZero_Estimated_ShowsStrengthZero()
    {
        var vm = Vm(LightingStatus.StrengthZero, fills: 0, image: @"C:\pics\a.png", estimated: true);
        Assert.Equal("Light strength is 0, so no lights are added.", vm.StatusText);
        Assert.DoesNotContain("Estimated", vm.StatusText);
    }

    [Fact] // [EDGE-ldr-light-estimate-33]
    public void NoLightInfoOverride_UsesSelectedFileName()
    {
        // The selection can't be lit (an unknown kind): the override names the selected file, not the drawn one.
        var vm = Vm(LightingStatus.OnWithKey, file: "old.png", image: @"C:\Users\someone\pics\scan.tga");
        Assert.Equal(LightingStatus.NoLightInfo, vm.ShownStatus);
        Assert.Equal("scan.tga: Milieu can't read any light from this image.", vm.StatusText);
        Assert.DoesNotContain("someone", vm.StatusText);
        // From the live status (a bundled scene without a valid rig): its display name.
        Assert.Equal("Moon: Milieu can't read any light from this image.", Vm(LightingStatus.NoLightInfo, file: "Moon", image: "bundled:moon").StatusText);
        Assert.Equal("Moon", Milieu.Lighting.SceneLighting.FileOf("bundled:moon"));
        Assert.Equal("scan.tga", Milieu.Lighting.SceneLighting.FileOf(@"C:\Users\someone\pics\scan.tga"));
    }

    [Fact] // [EDGE-ldr-light-estimate-34]
    public void EstimatedNote_HiddenWhileOff()
    {
        foreach (var image in new[] { @"C:\pics\a.jpg", "bundled:moon" })
        {
            Assert.DoesNotContain(LightingViewModel.EstimatedNote, Vm(LightingStatus.Off, image: image, on: false).Notes);
            Assert.Equal(["Only you see these lights."], Vm(LightingStatus.Off, image: image, on: false).Notes);
        }
    }

    [Fact] // [AC-scene-lighting-5]
    public void NoKey_ShowsNoClearSunText()
    {
        Assert.Equal("Lit by the image: no clear sun, 2 fill lights.", Vm(LightingStatus.OnNoKey).StatusText);
        Assert.Equal("Lit by the image: sun + 3 fill lights.", Vm(LightingStatus.OnWithKey, fills: 3).StatusText);
    }

    [Fact] // [AC-scene-lighting-6]
    public void AlwaysShowsOnlyYouNote()
    {
        foreach (var status in new[] { LightingStatus.Off, LightingStatus.WaitingForGPose, LightingStatus.OnWithKey, LightingStatus.CrashGuard })
        {
            Assert.Equal(["Only you see these lights."], Vm(status).Notes);
            Assert.Equal(["Only you see these lights."], Vm(status, on: false).Notes);
        }
        Assert.Empty(Vm(LightingStatus.OnWithKey, available: false).Notes);
    }

    [Theory] // [AC-scene-lighting-6]
    [InlineData(DeliveryMode.Cushion)]
    [InlineData(DeliveryMode.MadBirdRing)]
    public void SyncedDelivery_AddsPairsNote(DeliveryMode mode)
    {
        Assert.Equal(["Only you see these lights.", "PSync pairs see the backdrop but not these lights."], Vm(LightingStatus.OnWithKey, delivery: mode).Notes);
        Assert.DoesNotContain(LightingViewModel.PairsNote, Vm(LightingStatus.OnWithKey, delivery: DeliveryMode.Local).Notes);
    }

    [Fact] // [AC-scene-lighting-7]
    public void CreateFailed_Text()
    {
        var vm = Vm(LightingStatus.CreateFailed);
        Assert.Equal("The game didn't create the lights; scene lighting is off until you untick and tick it again.", vm.StatusText);
        Assert.Equal(Severity.Error, vm.StatusSeverity);
        Assert.True(vm.ControlsEnabled);
    }

    [Fact] // [AC-scene-lighting-7]
    public void Unavailable_ControlsDisabled_Text()
    {
        foreach (var on in new[] { true, false })
        foreach (var image in new[] { Hdr, @"C:\pics\a.png" })
        {
            var vm = Vm(LightingStatus.Unavailable, on: on, image: image);
            Assert.False(vm.ControlsEnabled);
            Assert.Equal("Milieu can't find the game's light functions. It may need an update for this game patch.", vm.StatusText);
            Assert.True(vm.Visible);
        }
    }

    [Fact] // [AC-scene-lighting-7] [EDGE-scene-lighting-33]
    public void CrashGuard_Text()
    {
        Assert.Equal("Scene lighting was switched off because the game closed while Milieu was setting up its lights last time. Untick and tick it again to retry.",
                     Vm(LightingStatus.CrashGuard).StatusText);
        Assert.Equal(LightingViewModel.CrashGuardText, Vm(LightingStatus.CrashGuard, image: @"C:\pics\a.jpg").StatusText);
    }

    [Fact] // [AC-scene-lighting-8]
    public void Analysing_ShowsFileNameOnly()
    {
        var vm = Vm(LightingStatus.Analysing, file: "sky.hdr");
        Assert.Equal("Reading the light in sky.hdr…", vm.StatusText);
        Assert.DoesNotContain(@"\", vm.StatusText);
        Assert.DoesNotContain("someone", vm.StatusText);
        // SceneLighting hands the view model the file name only.
        Assert.Equal("sky.hdr", System.IO.Path.GetFileName(Hdr));
    }

    [Fact] // [AC-scene-lighting-9]
    public void WaitingForGpose_Text()
    {
        Assert.Equal("Lights appear in GPose.", Vm(LightingStatus.WaitingForGPose).StatusText);
        Assert.Equal("Lights appear while a backdrop is shown.", Vm(LightingStatus.WaitingForBackdrop).StatusText);   // ldr-light-estimate
        Assert.Equal("", Vm(LightingStatus.Off, on: false).StatusText);
    }

    [Fact] // [AC-scene-lighting-10] [EDGE-scene-lighting-28]
    public void KillSwitchOff_SectionHidden()
    {
        var vm = Vm(LightingStatus.OnWithKey, available: false);
        Assert.False(vm.Visible);
        Assert.Empty(vm.Notes);
        Assert.True(Vm(LightingStatus.Off).Visible);
    }

    [Fact] // [EDGE-scene-lighting-4]
    public void AllBlack_ShowsNoClearSunZeroFills()
    {
        Assert.Equal("Lit by the image: no clear sun, 0 fill lights.", Vm(LightingStatus.OnNoKey, fills: 0).StatusText);
    }

    [Fact] // [EDGE-scene-lighting-14]
    public void ReadFailed_Text()
    {
        var vm = Vm(LightingStatus.ReadFailed, file: "dusk.exr");
        Assert.Equal("dusk.exr: couldn't read its light information.", vm.StatusText);
        Assert.Equal(Severity.Error, vm.StatusSeverity);
    }

    [Fact] // [EDGE-scene-lighting-36]
    public void KeyNotDrawn_Text()
    {
        var vm = Vm(LightingStatus.OnKeyNotDrawn, fills: 1);
        Assert.Equal("Lit by the image: the sun isn't on the dome at these repeat settings, 1 fill lights.", vm.StatusText);
    }

    [Fact] // [EDGE-scene-lighting-38]
    public void StrengthZeroWithSun_ShowsStrengthZeroNotNoClearSun()
    {
        var vm = Vm(LightingStatus.StrengthZero, fills: 0);
        Assert.Equal("Light strength is 0, so no lights are added.", vm.StatusText);
        Assert.DoesNotContain("no clear sun", vm.StatusText);
    }

    [Fact] // [AC-synced-lighting-4]
    public void InSyncedBackdrop_SaysPairsSeeTheLights()
    {
        var vm = Vm(LightingStatus.InSyncedBackdrop, delivery: DeliveryMode.MadBirdRing);
        Assert.Equal(LightingViewModel.InSyncedBackdropText, vm.StatusText);
        Assert.Contains(LightingViewModel.SyncedLightsNote, vm.Notes);
        Assert.DoesNotContain(LightingViewModel.OnlyYouNote, vm.Notes);
        Assert.DoesNotContain(LightingViewModel.PairsNote, vm.Notes);
    }

    [Theory] // [AC-synced-lighting-2]
    [InlineData("toggle")]
    [InlineData("strength")]
    [InlineData("fills")]
    public void LightChange_SetsSavesAndRequestsRebuild(string control)
    {
        var config = new Configuration();
        int saves = 0, requests = 0;
        System.Action<Configuration> set = control switch
        {
            "toggle" => c => c.SceneLighting = false,
            "strength" => c => c.LightStrength = 1.5f,
            _ => c => c.FillLightCount = 1,
        };
        LightingViewModel.LightChange(config, set, () => saves++, () => requests++)();
        Assert.Equal((1, 1), (saves, requests));
        Assert.True(control switch { "toggle" => !config.SceneLighting, "strength" => config.LightStrength == 1.5f, _ => config.FillLightCount == 1 });
    }

    [Fact] // [EDGE-synced-lighting-38]
    public void InSyncedBackdropInGPose_ShowsDeferralNote()
    {
        var vm = new LightingViewModel(new LightingStatusSnapshot(LightingStatus.InSyncedBackdrop, "sky.hdr", GPose: true),
                                       new Configuration { ImagePath = Hdr }, true, DeliveryMode.MadBirdRing, false);
        Assert.Contains(LightingViewModel.GPoseDeferralNote, vm.Notes);
        Assert.DoesNotContain(LightingViewModel.SyncedLightsNote, vm.Notes);
    }
    [Fact] // [AC-local-dome-lights-7]
    public void InLocalBackdrop_TextAndNotes()
    {
        var vm = Vm(LightingStatus.InLocalBackdrop, localLights: true);
        Assert.Equal("Lit by the backdrop's own lights.", vm.StatusText);
        Assert.Equal([LightingViewModel.LocalLightsNote, LightingViewModel.OnlyYouNote], vm.Notes);
        Assert.DoesNotContain(LightingViewModel.PairsNote, vm.Notes);
    }

    [Theory] // [AC-local-dome-lights-7]
    [InlineData(LightingStatus.InLocalBackdrop, true)]
    [InlineData(LightingStatus.OnWithKey, true)]
    [InlineData(LightingStatus.OnNoKey, true)]
    [InlineData(LightingStatus.OnKeyNotDrawn, true)]
    [InlineData(LightingStatus.StrengthZero, true)]
    [InlineData(LightingStatus.WaitingForGPose, false)]
    [InlineData(LightingStatus.WaitingForBackdrop, false)]
    [InlineData(LightingStatus.Analysing, false)]
    public void LocalLightsNote_OnlyWithInLocalBackdropOrGPoseNativeStatus(LightingStatus status, bool shown)
    {
        Assert.Equal(shown, System.Linq.Enumerable.Contains(Vm(status, localLights: true).Notes, LightingViewModel.LocalLightsNote));
        Assert.DoesNotContain(LightingViewModel.LocalLightsNote, Vm(status, localLights: false).Notes);
        Assert.DoesNotContain(LightingViewModel.LocalLightsNote, Vm(status, localLights: true, delivery: DeliveryMode.MadBirdRing).Notes);
    }

    [Fact] // [AC-local-dome-lights-7] [EDGE-local-dome-lights-14] [EDGE-local-dome-lights-29]
    public void FlagOff_TodaysTexts()
    {
        var vm = Vm(LightingStatus.OnWithKey, localLights: false);
        Assert.Equal([LightingViewModel.OnlyYouNote], vm.Notes);
        Assert.Equal(LightingViewModel.WaitingForGPoseText, Vm(LightingStatus.WaitingForGPose, localLights: false).StatusText);
    }

    [Fact] // [EDGE-local-dome-lights-19]
    public void InLocalBackdrop_EstimatedImage_ShowsEstimatedNote()
    {
        var vm = Vm(LightingStatus.InLocalBackdrop, image: @"C:\pics\room.jpg", file: "room.jpg", estimated: true, localLights: true);
        Assert.Equal([LightingViewModel.EstimatedNote, LightingViewModel.LocalLightsNote, LightingViewModel.OnlyYouNote], vm.Notes);
    }

    [Fact] // [EDGE-local-dome-lights-22]
    public void Unticked_ShowsOffImmediately()
    {
        var vm = Vm(LightingStatus.InLocalBackdrop, on: false, localLights: true);
        Assert.Equal(LightingStatus.Off, vm.ShownStatus);
        Assert.DoesNotContain(LightingViewModel.LocalLightsNote, vm.Notes);
    }
}
