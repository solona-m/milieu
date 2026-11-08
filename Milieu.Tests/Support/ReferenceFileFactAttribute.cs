using System;
using System.IO;
using Xunit;

namespace Milieu.Tests.Support;

/// <summary>
/// A fact that needs a local reference file named by an environment variable (never a hard-coded path).
/// It skips when the variable is unset or the file is missing. The pre-review run sets them (AGENTS.md / plan).
/// </summary>
public sealed class ReferenceFileFactAttribute : FactAttribute
{
    public ReferenceFileFactAttribute(params string[] envVars)
    {
        foreach (var v in envVars)
        {
            var path = Environment.GetEnvironmentVariable(v);
            if (string.IsNullOrEmpty(path) || !(File.Exists(path) || Directory.Exists(path)))
            {
                Skip = $"Set {v} to the reference file to run this test.";
                return;
            }
        }
    }
}

public static class ReferenceFiles
{
    public const string Avfx = "MILIEU_REFERENCE_AVFX";
    public const string Atex = "MILIEU_REFERENCE_ATEX";
    public const string HdriSourceDir = "MILIEU_HDRI_SOURCE_DIR";

    public static string Path(string envVar) =>
        Environment.GetEnvironmentVariable(envVar) ?? throw new InvalidOperationException($"{envVar} is not set.");
}
