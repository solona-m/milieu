using System;
using System.IO;

namespace Milieu.Tests.Support;

/// <summary>Finds files in the repository checkout (product assets and workflow), relative to the test binaries.</summary>
public static class RepoPaths
{
    public static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Milieu.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("Repository root (Milieu.slnx) not found above the test output.");
    }

    public static string DefaultBackdrops() => Path.Combine(Root(), "Milieu", "DefaultBackdrops");
}
