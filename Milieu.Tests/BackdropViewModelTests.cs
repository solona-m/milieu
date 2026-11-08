using System.Collections.Generic;
using Milieu.Backdrop;
using Milieu.Gui;
using Xunit;

namespace Milieu.Tests;

public class BackdropViewModelTests
{
    private static BackdropStatus St(BackdropState s, StatusMessage? m = null, params StatusMessage[] warnings) =>
        new(s, 0, m ?? StatusMessage.None, warnings);

    private static Configuration Cfg(string? path = @"C:\x\pano.jpg") => new() { ImagePath = path };

    [Fact] // [AC-hdri-backdrop-2]
    public void RoomMode_ShowsInsideDomeHint()
    {
        var c = Cfg();
        Assert.False(new BackdropViewModel(St(BackdropState.Hidden), MessageKey.None, c).ShowRoomHint);
        c.Mode = DomeMode.Room;
        Assert.True(new BackdropViewModel(St(BackdropState.Hidden), MessageKey.None, c).ShowRoomHint);
        Assert.Equal("Keep the camera inside the dome for an undistorted view.", BackdropViewModel.RoomHint);
    }

    [Fact] // [AC-hdri-backdrop-3]
    public void SliderBehaviour_Flags()
    {
        // All on release: yaw/height respawn the dome, the rest rebuild it (a live move leaves the particle behind).
        foreach (var s in new[] { "Yaw", "Height", "Radius", "VerticalScale", "HorizontalRepeat", "VerticalRepeat", "Brightness" })
            Assert.Equal(SliderApply.OnRelease, BackdropViewModel.ApplyOf(s));
    }

    [Fact] // [AC-hdri-backdrop-5]
    public void HdrControls_EnabledOnlyForHdr()
    {
        Assert.True(new BackdropViewModel(St(BackdropState.Hidden), MessageKey.None, Cfg(@"C:\a.HDR")).HdrControlsEnabled);
        Assert.False(new BackdropViewModel(St(BackdropState.Hidden), MessageKey.None, Cfg(@"C:\a.png")).HdrControlsEnabled);
        Assert.False(new BackdropViewModel(St(BackdropState.Hidden), MessageKey.None, Cfg(@"C:\a.jpg")).HdrControlsEnabled);
        Assert.False(new BackdropViewModel(St(BackdropState.Hidden), MessageKey.None, Cfg(null)).HdrControlsEnabled);
    }

    [Fact] // [AC-hdri-backdrop-9]
    public void Blockers_DisableShowWithMessage()
    {
        foreach (var key in new[] { MessageKey.InteropUnavailable, MessageKey.PenumbraMissing, MessageKey.PenumbraIncompatible,
                                    MessageKey.PenumbraModsDisabled, MessageKey.PenumbraNoBaseCollection, MessageKey.NotLoggedIn })
        {
            var vm = new BackdropViewModel(St(BackdropState.Hidden), key, Cfg());
            Assert.False(vm.CanShow);
            Assert.Equal(key, vm.Status.Key);
            Assert.Equal(Severity.Error, vm.StatusSeverity);
        }
        Assert.True(new BackdropViewModel(St(BackdropState.Hidden), MessageKey.None, Cfg()).CanShow);
        Assert.False(new BackdropViewModel(St(BackdropState.Hidden), MessageKey.None, Cfg(null)).CanShow);
    }

    [Fact] // [EDGE-hdri-backdrop-8]
    public void LargeImage_WarningShown()
    {
        var w = StatusMessage.Of(MessageKey.LargeImage, ("file", "sunset.jpeg"), ("w", 21184), ("h", 3848));
        var vm = new BackdropViewModel(St(BackdropState.Shown, null, w), MessageKey.None, Cfg());
        Assert.Equal(new List<string> { "sunset.jpeg is very large (21184x3848); converting it takes longer and uses a lot of memory." }, vm.WarningTexts);
        Assert.Equal(Severity.Warning, w.Severity);
    }

    [Fact] // [EDGE-hdri-backdrop-31]
    public void Messages_UseFileNameOnly()
    {
        var c = Cfg(@"C:\Users\someone\secret\pano.jpg");
        var vm = new BackdropViewModel(St(BackdropState.Hidden), MessageKey.None, c);
        Assert.Equal("pano.jpg", vm.ImageFileName);
        var ex = Assert.Throws<MilieuException>(() => Milieu.Imaging.ImageLoader.Probe(c.ImagePath!));
        Assert.DoesNotContain("secret", ex.Status.Text);
        Assert.Equal("Show", vm.ShowButtonLabel);
        Assert.Equal("Apply", new BackdropViewModel(St(BackdropState.Shown), MessageKey.None, c).ShowButtonLabel);
    }
}
