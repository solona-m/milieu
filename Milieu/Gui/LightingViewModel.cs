using System.Collections.Generic;
using Milieu.Assets;
using Milieu.Backdrop;
using Milieu.Imaging;
using Milieu.Lighting;
using Milieu.Sync;

namespace Milieu.Gui;

/// <summary>
/// Pure state and texts of the "Scene lighting" block in Settings (unit-tested). Drawn only when the kill switch
/// (Plugin.LightingAvailable) is on; its controls are disabled while the game's light functions are missing.
/// </summary>
public sealed class LightingViewModel(LightingStatusSnapshot snapshot, Configuration config, bool lightingAvailable, DeliveryMode effective)
{
    public const string Header = "Scene lighting";
    public const string ToggleLabel = "Light the scene from the image";
    public const string StrengthLabel = "Light strength";
    public const string FillsLabel = "Fill lights";

    public const string UnavailableText = "Milieu can't find the game's light functions. It may need an update for this game patch.";
    public const string CrashGuardText =
        "Scene lighting was switched off because the game closed while Milieu was setting up its lights last time. Untick and tick it again to retry.";
    public const string NeedsHdrText = "Only .hdr and .exr images carry the light information this needs.";
    public const string WaitingForBackdropText = "Lights appear while an HDR backdrop is shown.";
    public const string WaitingForGPoseText = "Lights appear in GPose.";
    public const string AnalysingTemplate = "Reading the light in {file}…";
    public const string CreateFailedText = "The game didn't create the lights; scene lighting is off until you untick and tick it again.";
    public const string ReadFailedTemplate = "{file}: couldn't read its light information.";
    public const string StrengthZeroText = "Light strength is 0, so no lights are added.";
    public const string OnWithKeyTemplate = "Lit by the image: sun + {n} fill lights.";
    public const string OnNoKeyTemplate = "Lit by the image: no clear sun, {n} fill lights.";
    public const string OnKeyNotDrawnTemplate = "Lit by the image: the sun isn't on the dome at these repeat settings, {n} fill lights.";
    public const string OnlyYouNote = "Only you see these lights.";
    public const string PairsNote = "PSync pairs see the backdrop but not these lights.";

    /// <summary>The section is drawn at all (the kill switch).</summary>
    public bool Visible => lightingAvailable;

    /// <summary>The checkbox and sliders are usable (disabled while the light functions are missing).</summary>
    public bool ControlsEnabled => snapshot.Status != LightingStatus.Unavailable;

    private bool SelectedIsHdr => config.ImagePath is { } p && !BackdropPins.IsBundledId(p) && ImageLoader.KindOf(p)?.IsHdr() == true;

    /// <summary>The status the panel shows: problems first, then a non-HDR selection, then the live lighting status.</summary>
    public LightingStatus ShownStatus => snapshot.Status switch
    {
        LightingStatus.Unavailable or LightingStatus.CrashGuard => snapshot.Status,
        _ when config.ImagePath != null && !SelectedIsHdr => LightingStatus.NeedsHdr,
        _ when !config.SceneLighting => LightingStatus.Off,
        _ => snapshot.Status,
    };

    public string StatusText => ShownStatus switch
    {
        LightingStatus.Unavailable => UnavailableText,
        LightingStatus.CrashGuard => CrashGuardText,
        LightingStatus.NeedsHdr => NeedsHdrText,
        LightingStatus.WaitingForBackdrop => WaitingForBackdropText,
        LightingStatus.WaitingForGPose => WaitingForGPoseText,
        LightingStatus.Analysing => AnalysingTemplate.Replace("{file}", snapshot.FileName),
        LightingStatus.CreateFailed => CreateFailedText,
        LightingStatus.ReadFailed => ReadFailedTemplate.Replace("{file}", snapshot.FileName),
        LightingStatus.StrengthZero => StrengthZeroText,
        LightingStatus.OnWithKey => OnWithKeyTemplate.Replace("{n}", snapshot.Fills.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        LightingStatus.OnNoKey => OnNoKeyTemplate.Replace("{n}", snapshot.Fills.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        LightingStatus.OnKeyNotDrawn => OnKeyNotDrawnTemplate.Replace("{n}", snapshot.Fills.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        _ => "",
    };

    public Severity StatusSeverity => ShownStatus switch
    {
        LightingStatus.Unavailable or LightingStatus.CreateFailed or LightingStatus.ReadFailed => Severity.Error,
        LightingStatus.CrashGuard or LightingStatus.OnKeyNotDrawn => Severity.Warning,
        _ => Severity.Info,
    };

    /// <summary>"Only you see these lights." whenever the section is usable, plus the PSync note under synced delivery.</summary>
    public IReadOnlyList<string> Notes
    {
        get
        {
            if (!Visible || !ControlsEnabled) return [];
            return effective == DeliveryMode.Local ? [OnlyYouNote] : [OnlyYouNote, PairsNote];
        }
    }
}
