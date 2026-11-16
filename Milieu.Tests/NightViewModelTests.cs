using System.Collections.Generic;
using Milieu.Backdrop;
using Milieu.Gui;
using Milieu.Lighting;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public class NightViewModelTests
{
    [Theory] // [AC-synced-lighting-5] [AC-synced-lighting-6]
    [InlineData(NightStatus.On, "", Severity.Info)]
    [InlineData(NightStatus.Off, "", Severity.Info)]
    [InlineData(NightStatus.Waiting, "", Severity.Info)]
    [InlineData(NightStatus.NotInThisZone, NightViewModel.NotInThisZoneText, Severity.Info)]
    [InlineData(NightStatus.Unavailable, NightViewModel.UnavailableText, Severity.Error)]
    [InlineData(NightStatus.CrashGuard, NightViewModel.CrashGuardText, Severity.Warning)]
    public void StatusTexts(NightStatus status, string text, Severity severity)
    {
        var vm = new NightViewModel(status, true);
        Assert.Equal(text, vm.StatusText);
        Assert.Equal(severity, vm.StatusSeverity);
    }

    [Fact] // [EDGE-synced-lighting-26]
    public void KillSwitchOff_Hidden()
    {
        Assert.False(new NightViewModel(NightStatus.Off, false).Visible);
        Assert.True(new NightViewModel(NightStatus.Off, true).Visible);
    }

    [Fact] // [AC-synced-lighting-5]
    public void Change_SetsAndSaves()
    {
        var config = new Configuration();
        var saves = 0;
        NightViewModel.Change(config, true, () => saves++)();
        Assert.True(config.NightWhileShown);
        Assert.Equal(1, saves);
    }
}
