using Milieu.Backdrop;
using Milieu.Lighting;

namespace Milieu.Gui;

/// <summary>The "Game sun" block (docs/adr/0013): the checkbox, a status line and the note. Pure.</summary>
public sealed class SunOffViewModel(SunOffStatus status, bool available)
{
    public const string Header = "Game sun";
    public const string ToggleLabel = "Turn off the game's sun";
    public const string Note = "Turns off the game's sun and moon light on your screen while a backdrop is shown to you only. Outside GPose the scene then keeps only the game's ambient light; in GPose, Milieu's scene lights (when on) light it. The sun comes back when the backdrop is hidden.";
    public const string NotOnlyMeText = "The sun is turned off only when the backdrop is visible to \"Only me\"; PSync pairs would still see it.";
    public const string UnavailableText = "Milieu can't use the game's lighting here. It may need an update for this game patch.";
    public const string CrashGuardText = "The sun was left on because the game closed while it was off last time. Untick and tick it again to retry.";
    public const string StartingSlowText = "Waiting for the game's lighting update…";

    /// <summary>The block is drawn at all (the kill switch, Plugin.SunOffAvailable).</summary>
    public bool Visible => available;

    /// <summary>The checkbox is disabled when Milieu can't use the game's lighting.</summary>
    public bool ControlsEnabled => status != SunOffStatus.Unavailable;

    public string StatusText => status switch
    {
        SunOffStatus.NotOnlyMe => NotOnlyMeText,
        SunOffStatus.Unavailable => UnavailableText,
        SunOffStatus.CrashGuard => CrashGuardText,
        SunOffStatus.StartingSlow => StartingSlowText,
        _ => "",
    };

    public Severity StatusSeverity => status switch
    {
        SunOffStatus.Unavailable => Severity.Error,
        SunOffStatus.CrashGuard => Severity.Warning,
        _ => Severity.Info,
    };

    /// <summary>The window posts this when the checkbox changes: set, save (SunOff reads it on its next tick).</summary>
    public static System.Action Change(Configuration config, bool on, System.Action save) =>
        () =>
        {
            config.SunOffWhileShown = on;
            save();
        };
}
