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
                                        bool available = true, DeliveryMode delivery = DeliveryMode.Local) =>
        new(new LightingStatusSnapshot(status, file, fills), new Configuration { ImagePath = image, SceneLighting = on }, available, delivery);

    [Theory] // [AC-scene-lighting-3]
    [InlineData(@"C:\pics\sky.png")]
    [InlineData(@"C:\pics\sky.jpg")]
    [InlineData("bundled:moon")]
    public void LdrOrBundled_ShowsNeedsHdrText(string image)
    {
        foreach (var status in new[] { LightingStatus.Off, LightingStatus.WaitingForGPose, LightingStatus.NeedsHdr, LightingStatus.OnWithKey })
        {
            var vm = Vm(status, image: image);
            Assert.Equal("Only .hdr and .exr images carry the light information this needs.", vm.StatusText);
            Assert.Equal(LightingStatus.NeedsHdr, vm.ShownStatus);
        }
        Assert.Equal(LightingViewModel.NeedsHdrText, Vm(LightingStatus.Off, image: image, on: false).StatusText);
        // An HDR selected while an LDR image is still drawn: the live status says so too.
        Assert.Equal(LightingViewModel.NeedsHdrText, Vm(LightingStatus.NeedsHdr).StatusText);
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
        Assert.Equal("The game didn't create the lights; scene lighting is off until you turn it on again.", vm.StatusText);
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
        Assert.Equal("Scene lighting was switched off because the game closed while Milieu was setting up its lights last time. Turn it on again to retry.",
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
        Assert.Equal("Lights appear while an HDR backdrop is shown.", Vm(LightingStatus.WaitingForBackdrop).StatusText);
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
}
