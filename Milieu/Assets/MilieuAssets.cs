using System.Collections.Generic;

namespace Milieu.Assets;

/// <summary>
/// Where Milieu's release assets live. GitHub is always a source; an edge mirror, when one is configured, is tried
/// first. The mirror is empty for now: the shared dl.solona.info worker lives in the Proteus repo, and a new plugin
/// gets its own path prefix there (docs/adr/0004-bundled-backdrops-as-release-assets.md).
/// </summary>
public static class MilieuAssets
{
    /// <summary>Assumed repository (docs/adr/0004).</summary>
    public const string Repo = "solona-m/milieu";

    public const string GitHubBase = "https://github.com/" + Repo + "/releases/download/";

    /// <summary>Mirror base with a trailing slash, e.g. "https://dl.solona.info/milieu/". Empty = GitHub only.</summary>
    public const string MirrorBase = "";

    public static string[] BaseUrls(string tag) => BaseUrls(tag, MirrorBase);

    internal static string[] BaseUrls(string tag, string mirrorBase)
    {
        var list = new List<string>();
        if (!string.IsNullOrEmpty(mirrorBase)) list.Add(mirrorBase + tag + "/");
        list.Add(GitHubBase + tag + "/");
        return list.ToArray();
    }
}
