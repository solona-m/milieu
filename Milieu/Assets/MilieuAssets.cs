using System.Collections.Generic;

namespace Milieu.Assets;

/// <summary>
/// Where Milieu's release assets live: the dl.solona.info edge mirror first, then GitHub. The mirror is the shared
/// Cloudflare worker in the Proteus repo, which routes Milieu under its own prefix (`/milieu/&lt;tag&gt;/latest.zip`,
/// `/milieu/backdrops-v&lt;N&gt;/&lt;stem&gt;.atex`; docs/adr/0005-pre-converted-bundled-backdrops.md, ADR 0007). GitHub always
/// stays in the list behind it, so a mirror outage means a slower download, never a broken one.
/// </summary>
public static class MilieuAssets
{
    /// <summary>Assumed repository (docs/adr/0004).</summary>
    public const string Repo = "solona-m/milieu";

    public const string GitHubBase = "https://github.com/" + Repo + "/releases/download/";

    /// <summary>Mirror base with a trailing slash. Empty would mean GitHub only.</summary>
    public const string MirrorBase = "https://dl.solona.info/milieu/";

    public static string[] BaseUrls(string tag) => BaseUrls(tag, MirrorBase);

    internal static string[] BaseUrls(string tag, string mirrorBase)
    {
        var list = new List<string>();
        if (!string.IsNullOrEmpty(mirrorBase)) list.Add(mirrorBase + tag + "/");
        list.Add(GitHubBase + tag + "/");
        return list.ToArray();
    }
}
