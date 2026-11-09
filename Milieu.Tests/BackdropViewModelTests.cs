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
    public void ApplyOf_SingleSwapRouting()
    {
        // Single-swap routing, all on release: yaw respawns, height is the controller's call, the rest rebuild.
        Assert.Equal(SliderApply.Placement, BackdropViewModel.ApplyOf("Yaw"));
        Assert.Equal(SliderApply.Height, BackdropViewModel.ApplyOf("Height"));
        foreach (var s in new[] { "Radius", "VerticalScale", "HorizontalRepeat", "VerticalRepeat", "Flip", "Brightness", "Mode", "GroundProjection" })
            Assert.Equal(SliderApply.Rebuild, BackdropViewModel.ApplyOf(s));
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

    }

    [Fact] // [AC-hdri-backdrop-13]
    public void Bundled_ShowsNote_DisablesSizeCombo()
    {
        var c = Cfg("bundled:moon");
        var vm = new BackdropViewModel(St(BackdropState.Shown), MessageKey.None, c);
        Assert.True(vm.IsBundledSelection);
        Assert.False(vm.HdrControlsEnabled);
        Assert.Equal("Bundled scenes come pre-converted at 8192x4096, so the texture size and HDR settings don't apply.",
                     Messages.Template(MessageKey.BundledNote));
        Assert.False(new BackdropViewModel(St(BackdropState.Shown), MessageKey.None, Cfg(@"C:\a.hdr")).IsBundledSelection);
    }

    [Fact] // [AC-synced-delivery-1] [EDGE-synced-delivery-35]
    public void DeliveryOptions_HiddenWhenUnavailable_ShownWhenAvailable()
    {
        var c = Cfg();
        var hidden = new BackdropViewModel(St(BackdropState.Hidden), MessageKey.None, c, syncedAvailable: false);
        Assert.False(hidden.SyncedAvailable);
        Assert.Equal(["Only me"], hidden.DeliveryLabels);
        var offered = new BackdropViewModel(St(BackdropState.Hidden), MessageKey.None, c, syncedAvailable: true);
        Assert.True(offered.SyncedAvailable);
        Assert.Equal(3, offered.DeliveryLabels.Count);
    }

    [Fact] // [AC-synced-delivery-1] [AC-synced-delivery-10]
    public void DeliveryLabels_TooltipsAndHints()
    {
        var c = Cfg();
        var vm = new BackdropViewModel(St(BackdropState.Hidden), MessageKey.None, c, true);
        Assert.Equal(["Only me", "PSync: Plush Cushion (experimental)", "PSync: Mad Bird Ring (right hand) (experimental)"], vm.DeliveryLabels);
        const string tip = "Experimental. PSync uploads the image you show to its server and sends it to all your pairs, who see the dome around you after their next sync. Every change redraws your character.";
        Assert.Equal(tip, BackdropViewModel.DeliveryTooltip(Milieu.Sync.DeliveryMode.Cushion));
        Assert.Equal(tip, BackdropViewModel.DeliveryTooltip(Milieu.Sync.DeliveryMode.MadBirdRing));
        Assert.Null(BackdropViewModel.DeliveryTooltip(Milieu.Sync.DeliveryMode.Local));
        Assert.Equal("", vm.DeliveryHint);
        c.Delivery = Milieu.Sync.DeliveryMode.Cushion;
        Assert.Equal("Experimental. Summon your Plush Cushion. PSync uploads your image to its server and sends it to all your pairs, who see the dome around you after their next sync. Every change redraws your character.",
                     vm.DeliveryHint);
        c.Delivery = Milieu.Sync.DeliveryMode.MadBirdRing;
        Assert.Equal("Experimental. Wear the Mad Bird Ring on your right hand. PSync uploads your image to its server and sends it to all your pairs, who see the dome around you after their next sync. Every change redraws your character.",
                     vm.DeliveryHint);
    }

    [Fact] // [AC-synced-delivery-6]
    public void Recenter_DisabledInSynced_WithTooltip()
    {
        var c = Cfg();
        c.Delivery = Milieu.Sync.DeliveryMode.Cushion;
        var synced = new BackdropViewModel(St(BackdropState.Shown), MessageKey.None, c, true);
        Assert.False(synced.CanRecenter);
        Assert.Equal("The item carries the dome; move to move it.", synced.RecenterTooltip);
        c.Delivery = Milieu.Sync.DeliveryMode.Local;
        var local = new BackdropViewModel(St(BackdropState.Shown), MessageKey.None, c, true);
        Assert.True(local.CanRecenter);
        Assert.Null(local.RecenterTooltip);
    }

    [Fact] // [AC-synced-delivery-9]
    public void SyncMessages_TextAndSeverity()
    {
        var expected = new (MessageKey Key, Severity Sev)[]
        {
            (MessageKey.SyncNoModDirectory, Severity.Error), (MessageKey.SyncNoPlayerCollection, Severity.Error),
            (MessageKey.SyncGameDataUnreadable, Severity.Error), (MessageKey.SyncNoFreeVfxId, Severity.Error),
            (MessageKey.SyncRefused, Severity.Error), (MessageKey.SyncRedirectTimeout, Severity.Error),
            (MessageKey.SyncShownCushion, Severity.Info), (MessageKey.SyncShownRing, Severity.Info),
            (MessageKey.SyncUploadNotice, Severity.Info), (MessageKey.SyncSharedCollection, Severity.Warning),
        };
        foreach (var (key, sev) in expected)
        {
            Assert.False(string.IsNullOrEmpty(Messages.Template(key)));
            Assert.Equal(sev, Messages.SeverityOf(key));
        }
        Assert.Equal("Milieu couldn't read the game's data for the Plush Cushion.",
                     StatusMessage.Of(MessageKey.SyncGameDataUnreadable, ("item", "Plush Cushion")).Text);
        Assert.Equal("Penumbra refused the synced backdrop (code 11).", StatusMessage.Of(MessageKey.SyncRefused, ("code", 11)).Text);
        Assert.Equal("The Mad Bird Ring has no free effect slot for the backdrop.", StatusMessage.Of(MessageKey.SyncNoFreeVfxId, ("item", "Mad Bird Ring")).Text);
        Assert.StartsWith("pano.jpg isn't shared yet.", StatusMessage.Of(MessageKey.SyncUploadNotice, ("file", "pano.jpg")).Text);
    }

    [Fact] // [AC-synced-delivery-1]
    public void DeliveryChange_AppliedOnlyWhenThePostedActionRuns()
    {
        var c = Cfg();
        var calls = new System.Collections.Generic.List<string>();
        var action = BackdropViewModel.DeliveryChange(c, Milieu.Sync.DeliveryMode.MadBirdRing, () => calls.Add("save"), () => calls.Add("apply"));
        Assert.Equal(Milieu.Sync.DeliveryMode.Local, c.Delivery);   // nothing changes during draw
        Assert.Empty(calls);
        action();
        Assert.Equal(Milieu.Sync.DeliveryMode.MadBirdRing, c.Delivery);
        Assert.Equal(["save", "apply"], calls);
    }
}
