using Milieu.Lighting;

namespace Milieu.Gui;

/// <summary>The "Time of day" block (docs/adr/0012): the checkbox, a status line and the "only you" note. Pure.</summary>
public sealed class NightViewModel(NightStatus status, Configuration config, bool available)
{
    public const string Header = "Time of day";
    public const string ToggleLabel = "Night while the backdrop is shown";
    public const string Note = "Sets your game's time to night while a backdrop is shown, so the sun doesn't compete with the backdrop's lights. Only you see it; it comes back when the backdrop is hidden.";
    public const string NotInThisZoneText = "This area keeps its own time of day, so night isn't set here.";
    public const string UnavailableText = "Milieu can't find the game's time of day. It may need an update for this game patch. Untick and tick it again to retry.";
    public const string CrashGuardText = "Night was switched off because the game closed while it was on last time. Untick and tick it again to retry.";

    /// <summary>The block is drawn at all (the kill switch, Plugin.NightAvailable).</summary>
    public bool Visible => available;

    public string StatusText => status switch
    {
        NightStatus.NotInThisZone => NotInThisZoneText,
        NightStatus.Unavailable => UnavailableText,
        NightStatus.CrashGuard => CrashGuardText,
        _ => "",
    };

    public Severity StatusSeverity => status switch
    {
        NightStatus.Unavailable => Severity.Error,
        NightStatus.CrashGuard => Severity.Warning,
        _ => Severity.Info,
    };

    /// <summary>The window posts this when the checkbox changes: set, save (NightWhileShown reads it on its next tick).</summary>
    public static System.Action Change(Configuration config, bool on, System.Action save) =>
        () =>
        {
            config.NightWhileShown = on;
            save();
        };
}
