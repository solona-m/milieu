using System.Text.RegularExpressions;
using Milieu.Assets;
using Xunit;

namespace Milieu.Tests;

public class MilieuAssetsTests
{
    [Fact] // [AC-hdri-backdrop-11] [AC-plugin-release-4]
    public void BaseUrls_MirrorFirstThenGitHub()
    {
        Assert.Equal("https://dl.solona.info/milieu/", MilieuAssets.MirrorBase);
        Assert.Equal(new[] { "https://dl.solona.info/milieu/backdrops-v2/", "https://github.com/solona-m/milieu/releases/download/backdrops-v2/" },
                     MilieuAssets.BaseUrls("backdrops-v2"));
        // The pure overload: an empty mirror means GitHub only.
        Assert.Equal(new[] { "https://github.com/solona-m/milieu/releases/download/backdrops-v1/" }, MilieuAssets.BaseUrls("backdrops-v1", ""));
        Assert.Equal(new[] { "https://dl.example/milieu/backdrops-v1/", "https://github.com/solona-m/milieu/releases/download/backdrops-v1/" },
                     MilieuAssets.BaseUrls("backdrops-v1", "https://dl.example/milieu/"));
    }

    [Fact] // [EDGE-plugin-release-12]
    public void Pinned_tag_and_names_fit_the_mirror_route()
    {
        // The worker's /milieu/ route (Proteus worker/src/index.js); anything else would silently go to GitHub only.
        Assert.Matches(new Regex("^backdrops-v[0-9]{1,6}$"), BackdropPins.Tag);
        foreach (var pin in BackdropPins.All)
            Assert.Matches(new Regex(@"^[a-z0-9]{1,40}\.atex$"), BackdropPins.RemoteName(pin));
    }
}
