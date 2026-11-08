using System.Collections.Generic;
using System.Linq;
using Milieu.Backdrop;
using Milieu.Imaging;

namespace Milieu.Gui;

/// <summary>How a slider applies its value: live while dragging, or once on release.</summary>
public enum SliderApply { Live, OnRelease }

/// <summary>Pure UI state for the main window, derived from the controller status and the config (unit-tested).</summary>
public sealed class BackdropViewModel(BackdropStatus status, MessageKey blocker, Configuration config)
{
    public const string RoomHint = "Keep the camera inside the dome for an undistorted view.";

    public bool HasImage => !string.IsNullOrEmpty(config.ImagePath);

    public bool CanShow => blocker == MessageKey.None && HasImage;

    public bool CanHide => status.State != BackdropState.Hidden;

    public bool CanRecenter => status.State == BackdropState.Shown;

    public string ShowButtonLabel => status.State is BackdropState.Shown or BackdropState.Suspended or BackdropState.AwaitingRedirect
        ? "Apply" : "Show";

    public bool HdrControlsEnabled => config.ImagePath != null && ImageLoader.KindOf(config.ImagePath) == ImageKind.Hdr;

    public bool ShowRoomHint => config.Mode == DomeMode.Room;

    public string ImageFileName => HasImage ? System.IO.Path.GetFileName(config.ImagePath!) : "(none)";

    /// <summary>The blocker if any, otherwise the controller's message.</summary>
    public StatusMessage Status => blocker != MessageKey.None ? StatusMessage.Of(blocker) : status.Message;

    public string StatusText => Status.Text;

    public Severity StatusSeverity => Status.Severity;

    public IReadOnlyList<string> WarningTexts => status.Warnings.Select(w => w.Text).ToList();

    public float Progress => status.Progress;

    public bool IsConverting => status.State == BackdropState.Preparing;

    public static SliderApply ApplyOf(string slider) => slider switch
    {
        // Every slider applies on release: yaw and height respawn the dome (a moved emitter leaves its particle behind),
        // the rest rebuild the VFX.
        _ => SliderApply.OnRelease,
    };
}
