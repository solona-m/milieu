using System.IO;
using System.Text.RegularExpressions;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public class UploadBackdropsWorkflowTests
{
    [Fact] // [EDGE-hdri-backdrop-50]
    public void Publishes_committed_folder_on_backdrops_tags_only()
    {
        var yml = File.ReadAllText(Path.Combine(RepoPaths.Root(), ".github", "workflows", "upload-backdrops.yml"));
        Assert.Contains("tags:\n      - 'backdrops-*'", yml.Replace("\r\n", "\n"));
        Assert.DoesNotContain("workflow_dispatch", yml);
        Assert.DoesNotContain("branches:", yml);
        Assert.Contains(@"^backdrops-v\d+\z", yml);
        Assert.Contains("REF_NAME: ${{ github.ref_name }}", yml);
        Assert.Contains("gh release view", yml);
        Assert.Contains(@"Milieu\DefaultBackdrops", yml);
        Assert.Contains(@"\[TODO\]", yml);
        Assert.Contains("0xFF -or $b[1] -ne 0xD8", yml);
        Assert.Contains("--latest=false", yml);
        Assert.Contains("-replace ' ', '.'", yml);
        // Every action is pinned to a full commit SHA.
        foreach (Match m in Regex.Matches(yml, @"uses:\s*(\S+)"))
            Assert.Matches(@"@[0-9a-f]{40}$", m.Groups[1].Value);
    }
}
