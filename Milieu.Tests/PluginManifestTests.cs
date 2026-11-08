using System.IO;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public class PluginManifestTests
{
    private static string Csproj() => File.ReadAllText(Path.Combine(RepoPaths.Root(), "Milieu", "Milieu.csproj"));

    [Fact]
    public void BuildNumberIsPositive() => Assert.True(Plugin.BuildNumber > 0);

    [Fact] // [AC-plugin-release-3]
    public void Csproj_version_comes_from_the_stamp_and_changelog_from_the_environment()
    {
        var csproj = Csproj();
        Assert.Contains("<Version>$(MilieuVersion)</Version>", csproj);
        Assert.DoesNotContain("0.1.0.0", csproj);
        Assert.Contains("<Changelog Condition=\"'$(Changelog)' == ''\"></Changelog>", csproj);
        Assert.DoesNotContain("upload-backdrops.yml", csproj);
        Assert.True(Plugin.BuildNumber >= 16, $"BuildNumber {Plugin.BuildNumber}");
    }

    [Fact] // [AC-plugin-release-8]
    public void IconUrl_is_the_mirror_icon()
    {
        Assert.Contains("<IconUrl>https://dl.solona.info/milieu/icon.png</IconUrl>", Csproj());
        Assert.True(File.Exists(Path.Combine(RepoPaths.Root(), "Milieu", "images", "icon.png")));
    }
}
