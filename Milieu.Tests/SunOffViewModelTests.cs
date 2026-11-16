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
        var vm = new SunOffViewModel(status, true);
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
    }

    [Fact] // [AC-sun-off-5]
    public void Unavailable_ControlsDisabled_ErrorText()
    {
        Assert.False(new SunOffViewModel(SunOffStatus.Unavailable, true).ControlsEnabled);
        foreach (var s in Enum.GetValues<SunOffStatus>())
            if (s != SunOffStatus.Unavailable) Assert.True(new SunOffViewModel(s, true).ControlsEnabled);
    }

    [Fact] // [AC-sun-off-7] [EDGE-sun-off-3]
    public void KillSwitchOff_Hidden()
    {
        Assert.False(new SunOffViewModel(SunOffStatus.Off, false).Visible);
        Assert.True(new SunOffViewModel(SunOffStatus.Off, true).Visible);
    }
}
