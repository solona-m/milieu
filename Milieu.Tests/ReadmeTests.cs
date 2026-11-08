using System.IO;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

/// <summary>
/// README.md on test is live (the dl.solona.info worker serves it at /milieu). Once Milieu is listed as stable it must
/// not say it is unlisted, must keep the install steps, and never asks for testing builds (ADR 0007).
/// </summary>
public class ReadmeTests
{
    private static string Readme() => File.ReadAllText(Path.Combine(RepoPaths.Root(), "README.md"));

    [Fact] // [AC-plugin-release-9]
    public void Readme_no_longer_says_milieu_is_unlisted()
    {
        var readme = Readme();
        Assert.DoesNotContain("Milieu isn't listed in the repository just yet", readme);
        Assert.Contains("https://dl.solona.info/repo.json", readme);
        Assert.Contains("### Install Milieu", readme);
    }

    [Fact] // [AC-plugin-release-11]
    public void Readme_never_asks_for_testing_builds() =>
        Assert.DoesNotContain("Get plugin testing builds", Readme());
}
