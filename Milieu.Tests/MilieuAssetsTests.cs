using Milieu.Assets;
using Xunit;

namespace Milieu.Tests;

public class MilieuAssetsTests
{
    [Fact] // [AC-hdri-backdrop-11]
    public void BaseUrls_GitHubOnlyWhenMirrorEmpty_MirrorFirstOtherwise()
    {
        Assert.Equal(new[] { "https://github.com/solona-m/milieu/releases/download/backdrops-v1/" }, MilieuAssets.BaseUrls("backdrops-v1"));
        Assert.Equal(new[] { "https://dl.example/milieu/backdrops-v1/", "https://github.com/solona-m/milieu/releases/download/backdrops-v1/" },
                     MilieuAssets.BaseUrls("backdrops-v1", "https://dl.example/milieu/"));
        Assert.Equal("", MilieuAssets.MirrorBase);
    }
}
