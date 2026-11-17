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
/// <param name="localLightsAvailable">The backdrop's own lights are active (BackdropController.LocalLightsActive).</param>
public sealed class LightingViewModel(LightingStatusSnapshot snapshot, Configuration config, bool lightingAvailable, DeliveryMode effective,
                                      bool localLightsAvailable)
{
    public const string Header = "Scene lighting";
    public const string ToggleLabel = "Light the scene from the image";
    public const string StrengthLabel = "Light strength";
    public const string FillsLabel = "Fill lights";

    public const string UnavailableText = "Milieu can't find the game's light functions. It may need an update for this game patch.";
    public const string CrashGuardText =
        "Scene lighting was switched off because the game closed while Milieu was setting up its lights last time. Untick and tick it again to retry.";
    public const string NoLightInfoTemplate = "{file}: Milieu can't read any light from this image.";
    public const string WaitingForBackdropText = "Lights appear while a backdrop is shown.";
    public const string WaitingForGPoseText = "Lights appear in GPose.";
    public const string AnalysingTemplate = "Reading the light in {file}…";
    public const string CreateFailedText = "The game didn't create the lights; scene lighting is off until you untick and tick it again.";
    public const string ReadFailedTemplate = "{file}: couldn't read its light information.";
    public const string StrengthZeroText = "Light strength is 0, so no lights are added.";
    public const string OnWithKeyTemplate = "Lit by the image: sun + {n} fill lights.";
    public const string OnNoKeyTemplate = "Lit by the image: no clear sun, {n} fill lights.";
    public const string OnKeyNotDrawnTemplate = "Lit by the image: the sun isn't on the dome at these repeat settings, {n} fill lights.";
    public const string EstimatedWithKeyTemplate = "Estimated from the image: main light + {n} fill lights.";
    public const string EstimatedNoKeyTemplate = "Estimated from the image: no bright spot found, {n} fill lights.";
    public const string EstimatedKeyNotDrawnTemplate =
        "Estimated from the image: the main light isn't on the dome at these repeat settings, {n} fill lights.";
    public const string EstimatedNote =
        "This image has no real light data, so the lights are estimated from its brightest spots at a fixed brightness. "
        + "Each light sits in its spot's direction at a fixed distance, not where the lamp really is. "
        + "Lamps smaller than about a quarter of a degree (about 6 pixels in an 8K image) are missed.";
    public const string OnlyYouNote = "Only you see these lights.";
    public const string PairsNote = "PSync pairs see the backdrop but not these lights.";
    public const string InSyncedBackdropText = "Lit by the backdrop's own lights, which travel with it: PSync pairs see them too, without Milieu.";
    public const string InLocalBackdropText = "Lit by the backdrop's own lights.";
    public const string LocalLightsNote =
        "Outside GPose the backdrop carries these lights; in GPose Milieu's scene lights replace them, so the backdrop reloads when you enter or leave GPose.";
    public const string SyncedLightsNote = "Shown in and out of GPose. A light change rebuilds the backdrop, which redraws everyone.";
    public const string GPoseDeferralNote = "Light changes apply when you leave GPose; applying redraws everyone.";

    /// <summary>The section is drawn at all (the kill switch).</summary>
    public bool Visible => lightingAvailable;

    /// <summary>The checkbox and sliders are usable (disabled while the light functions are missing).</summary>
    public bool ControlsEnabled => snapshot.Status != LightingStatus.Unavailable;

    /// <summary>The selection is an image Milieu can read light from (HDR, PNG/JPG or a bundled scene).</summary>
    private bool SelectedCanLight => config.ImagePath is { } p && SceneLighting.CanLight(p);

    /// <summary>The selection gets estimated lights (PNG/JPG or a bundled scene, docs/adr/0010).</summary>
    private bool SelectedIsEstimated => config.ImagePath is { } p && SelectedCanLight && (BackdropPins.IsBundledId(p) || ImageLoader.KindOf(p)?.IsHdr() == false);

    private bool NoLightOverride => config.ImagePath != null && !SelectedCanLight;

    /// <summary>The status the panel shows: problems first, then a selection with no light to read, then the live status.</summary>
    public LightingStatus ShownStatus => snapshot.Status switch
    {
        LightingStatus.Unavailable or LightingStatus.CrashGuard => snapshot.Status,
        _ when NoLightOverride => LightingStatus.NoLightInfo,
        _ when !config.SceneLighting => LightingStatus.Off,
        _ => snapshot.Status,
    };

    private string Fills => snapshot.Fills.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string StatusText => ShownStatus switch
    {
        LightingStatus.Unavailable => UnavailableText,
        LightingStatus.CrashGuard => CrashGuardText,
        LightingStatus.NoLightInfo => NoLightInfoTemplate.Replace("{file}", NoLightOverride ? SceneLighting.FileOf(config.ImagePath!) : snapshot.FileName),
        LightingStatus.WaitingForBackdrop => WaitingForBackdropText,
        LightingStatus.WaitingForGPose => WaitingForGPoseText,
        LightingStatus.Analysing => AnalysingTemplate.Replace("{file}", snapshot.FileName),
        LightingStatus.CreateFailed => CreateFailedText,
        LightingStatus.ReadFailed => ReadFailedTemplate.Replace("{file}", snapshot.FileName),
        LightingStatus.StrengthZero => StrengthZeroText,
        LightingStatus.OnWithKey => (snapshot.Estimated ? EstimatedWithKeyTemplate : OnWithKeyTemplate).Replace("{n}", Fills),
        LightingStatus.OnNoKey => (snapshot.Estimated ? EstimatedNoKeyTemplate : OnNoKeyTemplate).Replace("{n}", Fills),
        LightingStatus.OnKeyNotDrawn => (snapshot.Estimated ? EstimatedKeyNotDrawnTemplate : OnKeyNotDrawnTemplate).Replace("{n}", Fills),
        LightingStatus.InSyncedBackdrop => InSyncedBackdropText,
        LightingStatus.InLocalBackdrop => InLocalBackdropText,
        _ => "",
    };

    public Severity StatusSeverity => ShownStatus switch
    {
        LightingStatus.Unavailable or LightingStatus.CreateFailed or LightingStatus.ReadFailed => Severity.Error,
        LightingStatus.CrashGuard or LightingStatus.OnKeyNotDrawn => Severity.Warning,
        _ => Severity.Info,
    };

    /// <summary>
    /// The local-lights note: only while the backdrop's own lights are active under Only me, and only beside a status that
    /// fits it (lit by the backdrop, or Milieu's GPose lights on), never beside "Lights appear in GPose.".
    /// </summary>
    private bool ShowsLocalLightsNote =>
        localLightsAvailable && effective == DeliveryMode.Local
        && ShownStatus is LightingStatus.InLocalBackdrop or LightingStatus.OnWithKey or LightingStatus.OnNoKey
            or LightingStatus.OnKeyNotDrawn or LightingStatus.StrengthZero;

    /// <summary>
    /// "Only you see these lights." whenever the section is usable, plus the PSync note under synced delivery; first, the
    /// estimated note while lighting is on with a PNG/JPG or bundled scene selected.
    /// </summary>
    public IReadOnlyList<string> Notes
    {
        get
        {
            if (!Visible || !ControlsEnabled) return [];
            var notes = new List<string>();
            if (config.SceneLighting && SelectedIsEstimated) notes.Add(EstimatedNote);
            if (ShownStatus == LightingStatus.InSyncedBackdrop)
            {
                // The synced dome carries the lights: pairs see them, so neither "only you" nor "pairs don't" applies.
                notes.Add(snapshot.GPose ? GPoseDeferralNote : SyncedLightsNote);
                return notes;
            }
            if (ShowsLocalLightsNote) notes.Add(LocalLightsNote);
            notes.Add(OnlyYouNote);
            if (effective != DeliveryMode.Local) notes.Add(PairsNote);
            return notes;
        }
    }

    /// <summary>
    /// The action the window posts when a lighting control changes: the config is set and saved, then a light rebuild is
    /// requested (the controller replans after 500 ms: a shown synced dome, or a shown local dome outside GPose).
    /// Native lights read the config on SceneLighting's next tick as before.
    /// </summary>
    public static System.Action LightChange(Configuration config, System.Action<Configuration> set, System.Action save, System.Action requestRebuild) =>
        () =>
        {
            set(config);
            save();
            requestRebuild();
        };
}
