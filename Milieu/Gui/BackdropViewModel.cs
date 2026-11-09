using System.Collections.Generic;
using System.Linq;
using Milieu.Backdrop;
using Milieu.Imaging;

namespace Milieu.Gui;

/// <summary>How a settings change is applied, always once (single-swap rule).</summary>
public enum SliderApply { Placement, Rebuild, Height }

/// <summary>Pure UI state for the main window, derived from the controller status and the config (unit-tested).</summary>
public sealed class BackdropViewModel(BackdropStatus status, MessageKey blocker, Configuration config, bool syncedAvailable = false)
{
    public const string UploadSentence =
        "PSync uploads the image you show to its server and sends it to all your pairs, who see the dome around you after their next sync. Every change redraws your character.";

    public const string HintUploadSentence =
        "PSync uploads your image to its server and sends it to all your pairs, who see the dome around you after their next sync. Every change redraws your character.";

    public const string RecenterSyncedTooltip = "The item carries the dome; move to move it.";

    private static readonly string[] AllLabels =
        ["Only me", "PSync: Plush Cushion (experimental)", "PSync: Mad Bird Ring (right hand) (experimental)"];

    /// <summary>Synced delivery is offered (testing and dev builds only); when false the "Visible to" group isn't drawn.</summary>
    public bool SyncedAvailable => syncedAvailable;

    /// <summary>The "Visible to" labels, in DeliveryMode order; only "Only me" when synced delivery isn't offered.</summary>
    public IReadOnlyList<string> DeliveryLabels => syncedAvailable ? AllLabels : AllLabels[..1];

    /// <summary>The delivery in force (as the controller's EffectiveDelivery).</summary>
    public Sync.DeliveryMode EffectiveDelivery => syncedAvailable ? config.Delivery : Sync.DeliveryMode.Local;

    /// <summary>The hover text of a PSync option: what it shares, and with whom. None for Only me.</summary>
    public static string? DeliveryTooltip(Sync.DeliveryMode mode) =>
        mode == Sync.DeliveryMode.Local ? null : "Experimental. " + UploadSentence;

    /// <summary>The hint under the radios for the selected option.</summary>
    public string DeliveryHint => EffectiveDelivery switch
    {
        Sync.DeliveryMode.Cushion => "Experimental. Summon your Plush Cushion. " + HintUploadSentence,
        Sync.DeliveryMode.MadBirdRing => "Experimental. Wear the Mad Bird Ring on your right hand. " + HintUploadSentence,
        _ => "",
    };

    /// <summary>
    /// The action the window posts when "Visible to" changes: the config is set, saved and applied on the framework tick,
    /// never during draw (the controller reads <c>config.Delivery</c> on that thread).
    /// </summary>
    public static System.Action DeliveryChange(Configuration config, Sync.DeliveryMode mode, System.Action save, System.Action apply) =>
        () =>
        {
            config.Delivery = mode;
            save();
            apply();
        };

    /// <summary>Why Re-centre is disabled in synced mode, or null.</summary>
    public string? RecenterTooltip => EffectiveDelivery != Sync.DeliveryMode.Local ? RecenterSyncedTooltip : null;

    public const string RoomHint = "Keep the camera inside the dome for an undistorted view.";

    public bool HasImage => !string.IsNullOrEmpty(config.ImagePath);

    public bool CanShow => blocker == MessageKey.None && HasImage;


    public bool CanRecenter => status.State == BackdropState.Shown && EffectiveDelivery == Sync.DeliveryMode.Local;


    public bool HdrControlsEnabled => config.ImagePath != null && !Assets.BackdropPins.IsBundledId(config.ImagePath)
                                      && ImageLoader.KindOf(config.ImagePath)?.IsHdr() == true;

    /// <summary>A bundled scene is selected: texture size and HDR settings don't apply (pre-converted).</summary>
    public bool IsBundledSelection => Assets.BackdropPins.IsBundledId(config.ImagePath);

    public bool ShowRoomHint => config.Mode == DomeMode.Room;

    public string ImageFileName => HasImage ? System.IO.Path.GetFileName(config.ImagePath!) : "(none)";

    /// <summary>The blocker if any, otherwise the controller's message.</summary>
    public StatusMessage Status => blocker != MessageKey.None ? StatusMessage.Of(blocker) : status.Message;

    public string StatusText => Status.Text;

    public Severity StatusSeverity => Status.Severity;

    public IReadOnlyList<string> WarningTexts => status.Warnings.Select(w => w.Text).ToList();

    public float Progress => status.Progress;

    public bool IsConverting => status.State == BackdropState.Preparing;

    /// <summary>
    /// Every change applies once, on release: yaw respawns the same VFX at the new placement; height lets the controller
    /// choose (rebuild if the ground-projection floor moves, otherwise respawn); everything else rebuilds the VFX.
    /// </summary>
    public static SliderApply ApplyOf(string setting) => setting switch
    {
        "Yaw" => SliderApply.Placement,
        "Height" => SliderApply.Height,
        _ => SliderApply.Rebuild,   // Radius, VerticalScale, HorizontalRepeat, VerticalRepeat, Flip, Brightness, Mode, GroundProjection
    };
}
