using System;
using Milieu.Backdrop;
using Milieu.Gui;
using Milieu.Lighting;
using Xunit;

namespace Milieu.Tests;

/// <summary>The "Game sun" block (docs/adr/0013).</summary>
public class SunOffViewModelTests
{
    [Theory] // [AC-sun-off-3] [EDGE-sun-off-30]
    [InlineData(SunOffStatus.Off, "", Severity.Info)]
    [InlineData(SunOffStatus.Waiting, "", Severity.Info)]
    [InlineData(SunOffStatus.Starting, "", Severity.Info)]
    [InlineData(SunOffStatus.On, "", Severity.Info)]
    [InlineData(SunOffStatus.NotOnlyMe, SunOffViewModel.NotOnlyMeText, Severity.Info)]
    [InlineData(SunOffStatus.StartingSlow, SunOffViewModel.StartingSlowText, Severity.Info)]
    [InlineData(SunOffStatus.Unavailable, SunOffViewModel.UnavailableText, Severity.Error)]
    [InlineData(SunOffStatus.CrashGuard, SunOffViewModel.CrashGuardText, Severity.Warning)]
    public void StatusTexts(SunOffStatus status, string text, Severity severity)
    {
        var vm = new SunOffViewModel(status, true, false);
        Assert.Equal(text, vm.StatusText);
        Assert.Equal(severity, vm.StatusSeverity);
        Assert.Equal(8, Enum.GetValues<SunOffStatus>().Length);   // every status has a row
    }

    [Fact] // [AC-sun-off-4]
    public void Change_SetsAndSaves()
    {
        var config = new Configuration();
        var saves = 0;
        SunOffViewModel.Change(config, false, () => saves++)();
        Assert.False(config.SunOffWhileShown);
        SunOffViewModel.Change(config, true, () => saves++)();
        Assert.True(config.SunOffWhileShown);
        Assert.Equal(2, saves);
    }

    [Fact] // [AC-sun-off-4]
    public void Labels()
    {
        Assert.Equal("Game sun", SunOffViewModel.Header);
        Assert.Equal("Turn off the game's sun", SunOffViewModel.ToggleLabel);
        Assert.Contains("ambient", SunOffViewModel.Note);
        Assert.DoesNotContain("take over", SunOffViewModel.Note);
        Assert.Equal("Turns off the game's sun and moon light on your screen while a backdrop is shown to you only. Outside GPose the scene then keeps only the game's ambient light; in GPose, Milieu's scene lights (when on) light it. The sun comes back when the backdrop is hidden.",
                     SunOffViewModel.Note);   // [AC-local-dome-lights-7] unchanged
        Assert.Equal("Turns off the game's sun and moon light on your screen while a backdrop is shown to you only. The game's ambient light stays; with scene lighting on, the backdrop's own lights light the scene outside GPose and Milieu's scene lights do in GPose. The sun comes back when the backdrop is hidden.",
                     SunOffViewModel.NoteWithDomeLights);
        Assert.DoesNotContain("only the game's ambient light", SunOffViewModel.NoteWithDomeLights);
    }

    [Fact] // [AC-local-dome-lights-7] [EDGE-local-dome-lights-14] [EDGE-local-dome-lights-29]
    public void NoteText_FollowsLocalLightsFlag()
    {
        Assert.Equal(SunOffViewModel.NoteWithDomeLights, new SunOffViewModel(SunOffStatus.On, true, true).NoteText);
        Assert.Equal(SunOffViewModel.Note, new SunOffViewModel(SunOffStatus.On, true, false).NoteText);
    }

    [Fact] // [AC-sun-off-5]
    public void Unavailable_ControlsDisabled_ErrorText()
    {
        Assert.False(new SunOffViewModel(SunOffStatus.Unavailable, true, false).ControlsEnabled);
        foreach (var s in Enum.GetValues<SunOffStatus>())
            if (s != SunOffStatus.Unavailable) Assert.True(new SunOffViewModel(s, true, false).ControlsEnabled);
    }

    [Fact] // [AC-sun-off-7] [EDGE-sun-off-3]
    public void KillSwitchOff_Hidden()
    {
        Assert.False(new SunOffViewModel(SunOffStatus.Off, false, false).Visible);
        Assert.True(new SunOffViewModel(SunOffStatus.Off, true, false).Visible);
    }
}
