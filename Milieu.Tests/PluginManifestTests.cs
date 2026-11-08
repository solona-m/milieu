using Xunit;

namespace Milieu.Tests;

public class PluginManifestTests
{
    [Fact]
    public void BuildNumberIsPositive() => Assert.True(Plugin.BuildNumber > 0);
}
