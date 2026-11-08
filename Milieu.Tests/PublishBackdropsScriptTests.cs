using System.IO;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public class PublishBackdropsScriptTests
{
    [Fact] // [EDGE-hdri-backdrop-50]
    public void Guards_present()
    {
        var script = File.ReadAllText(Path.Combine(RepoPaths.Root(), "tools", "publish-backdrops.ps1"));
        foreach (var required in new[]
                 {
                     "'^backdrops-v\\d+\\z'",
                     "git rev-parse \"$Tag^{commit}\"",
                     "git rev-parse HEAD",
                     "git diff --quiet HEAD -- Milieu/Assets/BackdropPins.cs",
                     "git ls-remote --exit-code --tags origin",
                     "gh release view",
                     "exit 1",
                     "-- verify",
                     "--tag $Tag",
                     "gh release create",
                     "--verify-tag",
                     "--latest=false",
                     "-Filter *.atex",
                 })
            Assert.Contains(required, script);
        Assert.DoesNotContain(".jpeg", script);
        Assert.False(File.Exists(Path.Combine(RepoPaths.Root(), ".github", "workflows", "upload-backdrops.yml")));
    }
}
