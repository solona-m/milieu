using System.IO;
using System.Reflection;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

/// <summary>Every build carries yyMM.&lt;git commit count&gt; (ADR 0007), as Proteus does.</summary>
public class VersionStampTests
{
    private static string Props() => File.ReadAllText(Path.Combine(RepoPaths.Root(), "Directory.Build.props"));

    [Fact] // [AC-plugin-release-3]
    public void Assembly_version_is_yyMM_dot_commit_count()
    {
        var asm = typeof(Plugin).Assembly;
        var v = asm.GetName().Version!;
        Assert.InRange(v.Major % 100, 1, 12);
        Assert.True(v.Major / 100 >= 26, v.ToString());
        Assert.True(v.Minor >= 1, v.ToString());
        Assert.Equal(0, v.Build);
        Assert.Equal(0, v.Revision);
        // The file version attribute keeps MSBuild's two-part form ("2611.42"); it is the same version.
        var file = System.Version.Parse(asm.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version);
        Assert.Equal((v.Major, v.Minor), (file.Major, file.Minor));
        Assert.True(file.Build <= 0 && file.Revision <= 0, file.ToString());
    }

    [Fact] // [AC-plugin-release-3]
    public void Props_stamp_Version_AssemblyVersion_FileVersion_from_the_commit_count()
    {
        var props = Props();
        Assert.Contains("<Target Name=\"ComputeMilieuVersion\" BeforeTargets=\"GetAssemblyVersion;GenerateNuspec;Build\"", props);
        Assert.Contains("git rev-list --count HEAD", props);
        Assert.Contains("ToString('yyMM')", props);
        foreach (var prop in new[] { "MilieuVersion", "Version", "AssemblyVersion", "FileVersion" })
            Assert.Contains($"<Output TaskParameter=\"Value\" PropertyName=\"{prop}\" />", props);
    }

    [Fact] // [EDGE-plugin-release-8]
    public void Props_fail_clearly_without_git()
    {
        var props = Props();
        Assert.Contains("2&gt;NUL", props);
        Assert.Contains("<Error Condition=\"!$([System.Text.RegularExpressions.Regex]::IsMatch('$(GitCommitCount)', '^\\d+$'))\"", props);
        Assert.Contains("could not read a commit count from git", props);
    }
}
