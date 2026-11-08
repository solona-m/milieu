using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

/// <summary>
/// Runs tools/check-package.ps1 (the release workflows' package guards, ADR 0007) under Windows PowerShell 5.1
/// against synthetic zips, so its behaviour is tested rather than its text.
/// </summary>
public sealed class PackageCheckScriptTests : System.IDisposable
{
    private readonly TempDir dir = new();
    public void Dispose() => dir.Dispose();

    private static string Script => Path.Combine(RepoPaths.Root(), "tools", "check-package.ps1");

    private string MakeZip(string name, string? manifestJson, bool dll = true, string manifestPath = "Milieu.json", int padding = 0)
    {
        var path = Path.Combine(dir.Path, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        if (manifestJson != null)
            using (var w = new StreamWriter(zip.CreateEntry(manifestPath).Open(), new UTF8Encoding(false)))
                w.Write(manifestJson);
        if (dll)
            using (var s = zip.CreateEntry("Milieu.dll").Open())
                s.Write(new byte[] { 0x4D, 0x5A, 0, 0 });
        if (padding > 0)
        {
            var rnd = new System.Random(1);
            var bytes = new byte[padding];
            rnd.NextBytes(bytes);   // incompressible
            using var s = zip.CreateEntry("Big.bin", CompressionLevel.NoCompression).Open();
            s.Write(bytes);
        }
        return path;
    }

    private static string Manifest(string internalName = "Milieu", string version = "2611.40.0.0", string? changelog = "Line 1\nLine 2")
    {
        var cl = changelog == null ? "" : $",\"Changelog\":\"{changelog.Replace("\n", "\\n")}\"";
        return $"{{\"Author\":\"Solona\",\"Name\":\"Milieu\",\"InternalName\":\"{internalName}\",\"AssemblyVersion\":\"{version}\"{cl}}}";
    }

    private static (int Code, string Output) Run(string zip, string version, string? changelog, long maxBytes, string shell = "powershell.exe")
    {
        var psi = new ProcessStartInfo(shell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script,
                                  "-Zip", zip, "-Version", version, "-MaxBytes", maxBytes.ToString() })
            psi.ArgumentList.Add(a);
        if (changelog != null) { psi.ArgumentList.Add("-Changelog"); psi.ArgumentList.Add(changelog); }
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    private const long FourMb = 4L * 1024 * 1024;

    [Fact] // [AC-plugin-release-1] [AC-plugin-release-2]
    public void Valid_package_passes()
    {
        var zip = MakeZip("ok.zip", Manifest());
        var (code, output) = Run(zip, "2611.40.0.0", "Line 1\r\nLine 2 ", FourMb);
        Assert.True(code == 0, output);
    }

    [Fact] // [EDGE-plugin-release-3]
    public void Missing_changelog_and_empty_environment_pass()
    {
        var zip = MakeZip("nochangelog.zip", Manifest(changelog: null));
        var (code, output) = Run(zip, "2611.40.0.0", null, FourMb);
        Assert.True(code == 0, output);
        (code, output) = Run(zip, "2611.40.0.0", "  ", FourMb);
        Assert.True(code == 0, output);
    }

    [Fact] // [EDGE-plugin-release-9]
    public void Oversized_package_fails()
    {
        var zip = MakeZip("big.zip", Manifest(), padding: 4096);
        var (code, output) = Run(zip, "2611.40.0.0", "Line 1\nLine 2", 1024);
        Assert.Equal(1, code);
        Assert.Contains("size", output, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Big.bin", output);
    }

    public static TheoryData<string, string> WrongManifests => new()
    {
        { "nested", "Milieu.json" },
        { "internalname", "InternalName" },
        { "version", "AssemblyVersion" },
        { "changelog", "Changelog" },
        { "nodll", "Milieu.dll" },
    };

    [Theory] // [EDGE-plugin-release-10]
    [MemberData(nameof(WrongManifests))]
    public void Wrong_manifest_fails(string variant, string named)
    {
        var zip = variant switch
        {
            "nested" => MakeZip("nested.zip", Manifest(), manifestPath: "sub/Milieu.json"),
            "internalname" => MakeZip("name.zip", Manifest(internalName: "Other")),
            "version" => MakeZip("ver.zip", Manifest(version: "2611.39.0.0")),
            "changelog" => MakeZip("cl.zip", Manifest(changelog: "Something else")),
            _ => MakeZip("nodll.zip", Manifest(), dll: false),
        };
        var (code, output) = Run(zip, "2611.40.0.0", "Line 1\nLine 2", FourMb);
        Assert.True(code == 1, $"{variant}: exit {code}: {output}");
        Assert.Contains(named, output);
    }

    // The workflows run the script under pwsh; these run it there too when pwsh is installed (it is on windows-latest).
    [PwshFact] // [AC-plugin-release-1] [EDGE-plugin-release-3] [EDGE-plugin-release-10]
    public void Pwsh_valid_missing_changelog_and_wrong_version()
    {
        var zip = MakeZip("ok.zip", Manifest());
        var (code, output) = Run(zip, "2611.40.0.0", "Line 1\r\nLine 2 ", FourMb, "pwsh");
        Assert.True(code == 0, output);
        var bare = MakeZip("bare.zip", Manifest(changelog: null));
        (code, output) = Run(bare, "2611.40.0.0", null, FourMb, "pwsh");
        Assert.True(code == 0, output);
        (code, output) = Run(zip, "2611.39.0.0", "Line 1\nLine 2", FourMb, "pwsh");
        Assert.True(code == 1, output);
        Assert.Contains("AssemblyVersion", output);
    }
}

/// <summary>A fact that needs pwsh (PowerShell 7) on PATH; it skips otherwise.</summary>
public sealed class PwshFactAttribute : FactAttribute
{
    public PwshFactAttribute()
    {
        var path = System.Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var d in path.Split(Path.PathSeparator))
            if (d.Length > 0 && File.Exists(Path.Combine(d.Trim('"'), "pwsh.exe"))) return;
        Skip = "pwsh is not on PATH.";
    }
}
