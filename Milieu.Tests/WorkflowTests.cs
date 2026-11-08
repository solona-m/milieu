using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

/// <summary>
/// The release workflows publish to every user, so their guards are pinned here (ADR 0007). Text checks run on the
/// lines outside comments, so a "why" comment may name what a workflow never does.
/// </summary>
public class WorkflowTests
{
    private const string Quick = "dotnet test Milieu.Tests/Milieu.Tests.csproj -c Release --filter \"Speed!=Slow\"";
    private const string Build = "dotnet build Milieu/Milieu.csproj -c Release";
    private const string ExitCheck = "if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }";

    private static string PathOf(string name) => Path.Combine(RepoPaths.Workflows(), name);
    private static string[] Raw(string name) => File.ReadAllLines(PathOf(name));
    private static List<string> Code(string name) => NonCommentLines(Raw(name)).Select(l => l.TrimEnd()).ToList();
    private static string CodeText(string name) => string.Join("\n", Code(name));
    private static IEnumerable<string> AllWorkflows() =>
        Directory.EnumerateFiles(RepoPaths.Workflows(), "*.yml").Select(Path.GetFileName).Select(n => n!);

    private static readonly string[] Releases = ["release.yml", "testing-release.yml"];

    // ---- helpers ported from Proteus SupplyChainTests (same author, AGPL) ----

    /// <summary>YAML lines with comments removed: whole-line comments dropped, trailing ones cut (a # outside quotes).</summary>
    internal static IEnumerable<string> NonCommentLines(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith('#')) continue;
            char quote = '\0';
            var cut = line.Length;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c is '"' or '\'') { quote = c; continue; }
                if (c == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1]))) { cut = i; break; }
            }
            yield return line[..cut];
        }
    }

    public enum UsesKind { NotUses, Local, Docker, PinnedWithComment, PinnedNoComment, Unpinned }

    private static readonly Regex UsesLine = new(@"^\s*(-\s*)?uses:\s*(\S+)(.*)$");
    private static readonly Regex PinnedRef = new(@"^[\w.-]+/[\w.-]+(/[\w./-]+)?@[0-9a-f]{40}$");
    private static readonly Regex VersionComment = new(@"^\s+#\s*v\d+(\.\d+)*");

    /// <summary>What a workflow line references. Works on the RAW line: the version comment is part of the check.</summary>
    internal static UsesKind ClassifyUsesLine(string line)
    {
        if (line.TrimStart().StartsWith('#')) return UsesKind.NotUses;
        var m = UsesLine.Match(line);
        if (!m.Success) return UsesKind.NotUses;
        var target = m.Groups[2].Value.Trim('"', '\'');
        if (target.StartsWith("./")) return UsesKind.Local;
        if (target.StartsWith("docker://")) return UsesKind.Docker;
        if (!PinnedRef.IsMatch(target)) return UsesKind.Unpinned;
        return VersionComment.IsMatch(m.Groups[3].Value) ? UsesKind.PinnedWithComment : UsesKind.PinnedNoComment;
    }

    /// <summary>The lines of the top-level <c>on:</c> block (comment-free), up to the next top-level key.</summary>
    internal static List<string> OnBlock(IEnumerable<string> lines)
    {
        var code = NonCommentLines(lines).Select(l => l.TrimEnd()).ToList();
        var start = code.FindIndex(l => l == "on:");
        Assert.True(start >= 0, "no top-level on: block");
        var block = new List<string>();
        for (var i = start + 1; i < code.Count; i++)
        {
            if (code[i].Length > 0 && !char.IsWhiteSpace(code[i][0])) break;
            if (code[i].Trim().Length > 0) block.Add(code[i]);
        }
        return block;
    }

    private static List<string> TagPatterns(IEnumerable<string> onBlock)
    {
        var lines = onBlock.ToList();
        var tags = lines.FindIndex(l => l.Trim() == "tags:");
        if (tags < 0) return [];
        var indent = lines[tags].Length - lines[tags].TrimStart().Length;
        var result = new List<string>();
        for (var i = tags + 1; i < lines.Count; i++)
        {
            var ind = lines[i].Length - lines[i].TrimStart().Length;
            if (ind <= indent) break;
            result.Add(lines[i].Trim().TrimStart('-').Trim().Trim('\'', '"'));
        }
        return result;
    }

    /// <summary>The <c>run:</c> blocks of a workflow: an inline <c>run: cmd</c>, or the lines indented deeper than <c>run: |</c>.</summary>
    internal static List<List<string>> RunBlocks(IEnumerable<string> lines)
    {
        var code = NonCommentLines(lines).Select(l => l.TrimEnd()).ToList();
        var blocks = new List<List<string>>();
        for (var i = 0; i < code.Count; i++)
        {
            var m = Regex.Match(code[i], @"^(\s*)(-\s*)?run:\s*(.*)$");
            if (!m.Success) continue;
            var rest = m.Groups[3].Value.Trim();
            if (rest is not ("|" or ">" or "|-" or ">-"))
            {
                blocks.Add([rest]);
                continue;
            }
            var indent = code[i].Length - code[i].TrimStart().Length;
            var block = new List<string>();
            for (var j = i + 1; j < code.Count; j++)
            {
                if (code[j].Trim().Length == 0) { block.Add(""); continue; }
                if (code[j].Length - code[j].TrimStart().Length <= indent) break;
                block.Add(code[j]);
            }
            while (block.Count > 0 && block[^1].Trim().Length == 0) block.RemoveAt(block.Count - 1);
            blocks.Add(block);
        }
        return blocks;
    }

    private static readonly Regex NativeStatement = new(@"^\s*(\$[\w:]+\s*=\s*)?\(*\s*(git|dotnet|&)\s");

    /// <summary>
    /// Native commands in statement position (incl. assignments) that are neither the block's last line nor followed
    /// by an exit-code check. pwsh carries on past a failing native command, so those would be silently ignored.
    /// A command used only inside an <c>if (...)</c> condition doesn't match (it fails closed; EDGE-24).
    /// </summary>
    internal static List<string> UncheckedNativeCommands(List<string> block)
    {
        var bad = new List<string>();
        var lines = block.Where(l => l.Trim().Length > 0).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!NativeStatement.IsMatch(lines[i])) continue;
            if (i == lines.Count - 1) continue;
            if (lines[i + 1].Trim() == ExitCheck) continue;
            bad.Add(lines[i].Trim());
        }
        return bad;
    }

    private static int IndexOf(List<string> lines, string needle, int from = 0)
    {
        for (var i = from; i < lines.Count; i++)
            if (lines[i].Contains(needle, StringComparison.Ordinal)) return i;
        return -1;
    }

    // ---- tests ----

    [Fact] // [AC-plugin-release-7]
    public void Every_action_is_pinned_to_a_commit_sha_with_a_version_comment()
    {
        var remote = 0;
        foreach (var name in AllWorkflows())
            foreach (var line in Raw(name))
            {
                var kind = ClassifyUsesLine(line);
                if (kind is UsesKind.NotUses or UsesKind.Local or UsesKind.Docker) continue;
                remote++;
                Assert.True(kind == UsesKind.PinnedWithComment, $"{name}: {line.Trim()} is {kind}");
            }
        Assert.True(remote >= 1, "no remote uses: found");
    }

    [Fact] // [AC-plugin-release-1]
    public void TestingRelease_publishes_a_prerelease_that_is_never_latest()
    {
        Assert.Equal(["testing-*"], TagPatterns(OnBlock(Raw("testing-release.yml"))));
        var code = Code("testing-release.yml");
        Assert.Contains(code, l => l.Trim() == "prerelease: true");
        Assert.Contains(code, l => l.Trim() == "make_latest: false");
        Assert.Contains(code, l => l.Trim() == "files: latest.zip");
        Assert.Contains("Copy-Item \"Milieu\\bin\\Release\\win-x64\\Milieu\\latest.zip\"", CodeText("testing-release.yml"));
    }

    [Fact] // [AC-plugin-release-2]
    public void Release_publishes_latest_only_for_an_annotated_tag_matching_the_built_version()
    {
        Assert.Equal(["v*"], TagPatterns(OnBlock(Raw("release.yml"))));
        var code = Code("release.yml");
        var text = CodeText("release.yml");
        var catFile = IndexOf(code, "(git cat-file -t $tag) -ne 'tag'");
        Assert.True(catFile >= 0);
        Assert.Contains("throw", code[catFile + 1]);
        var tagCheck = IndexOf(code, "$tag -ne \"v$version\"");
        Assert.True(tagCheck >= 0);
        Assert.Contains("throw", code[tagCheck + 1]);
        Assert.Contains(code, l => l.Trim() == "make_latest: true");
        Assert.DoesNotContain("prerelease: true", text);
        Assert.Contains(code, l => l.Trim() == "files: latest.zip");
    }

    [Theory] // [AC-plugin-release-1] [AC-plugin-release-2]
    [InlineData("release.yml")]
    [InlineData("testing-release.yml")]
    public void Both_release_workflows_test_build_check_then_publish(string name)
    {
        var code = Code(name);
        var test = IndexOf(code, Quick);
        var build = IndexOf(code, Build);
        var check = IndexOf(code, "tools/check-package.ps1 -Zip latest.zip -Version");
        var publish = IndexOf(code, "uses: softprops/action-gh-release@");
        Assert.True(test >= 0 && build >= 0 && check >= 0 && publish >= 0, $"{name}: {test} {build} {check} {publish}");
        Assert.True(test < build && build < check && check < publish, $"{name}: order {test} {build} {check} {publish}");
        Assert.Contains("-MaxBytes 4MB", code[check]);
    }

    [Theory] // [AC-plugin-release-1] [AC-plugin-release-2]
    [InlineData("release.yml")]
    [InlineData("testing-release.yml")]
    public void Both_release_workflows_notify_the_plugins_repo(string name)
    {
        var text = CodeText(name);
        Assert.Contains("repos/solona-m/plugins/dispatches", text);
        Assert.Contains("plugin-released", text);
        Assert.Contains("\"Milieu\"", text);
        Assert.Contains("secrets.PLUGINS_REPO_TOKEN", text);
        Assert.Contains("Invoke-RestMethod", text);
        Assert.DoesNotContain("-SkipHttpErrorCheck", text);
        Assert.DoesNotContain("-StatusCodeVariable", text);
        Assert.DoesNotContain("-ErrorAction", text);
        if (name == "testing-release.yml") Assert.Matches(@"testing\s*=\s*\$true", text);
        else Assert.DoesNotMatch(@"testing\s*=\s*\$true", text);
    }

    [Fact] // [AC-plugin-release-6]
    public void Test_workflow_runs_the_quick_suite_on_test_and_main_read_only()
    {
        var on = OnBlock(Raw("test.yml"));
        var push = on.FindIndex(l => l.Trim() == "push:");
        var pr = on.FindIndex(l => l.Trim() == "pull_request:");
        Assert.True(push >= 0 && pr >= 0);
        foreach (var at in new[] { push, pr })
        {
            var branches = on[at + 1];
            Assert.Contains("branches:", branches);
            Assert.Contains("test", branches);
            Assert.Contains("main", branches);
        }
        var code = Code("test.yml");
        var perms = code.FindIndex(l => l == "permissions:");
        Assert.True(perms >= 0);
        var permLines = code.Skip(perms + 1).TakeWhile(l => l.Length == 0 || char.IsWhiteSpace(l[0])).Where(l => l.Trim().Length > 0).ToList();
        Assert.Equal(["contents: read"], permLines.Select(l => l.Trim()));
        Assert.Contains(Quick, CodeText("test.yml"));
    }

    [Fact] // [EDGE-plugin-release-1]
    public void Release_rejects_a_lightweight_tag()
    {
        var code = Code("release.yml");
        var at = IndexOf(code, "(git cat-file -t $tag) -ne 'tag'");
        Assert.True(at >= 0);
        Assert.Contains("throw", code[at + 1]);
        Assert.Contains("git tag -a", code[at + 1]);
    }

    [Fact] // [EDGE-plugin-release-2]
    public void Release_rejects_a_tag_that_differs_from_the_built_version()
    {
        var code = Code("release.yml");
        var at = IndexOf(code, "$tag -ne \"v$version\"");
        Assert.True(at >= 0);
        Assert.Contains("throw", code[at + 1]);
        Assert.Contains("does not match the built version", code[at + 1]);
        Assert.True(at < IndexOf(code, "uses: softprops/action-gh-release@"));
    }

    [Fact] // [EDGE-plugin-release-3]
    public void TestingRelease_tolerates_a_lightweight_tag()
    {
        var code = Code("testing-release.yml");
        var at = IndexOf(code, "(git cat-file -t $tag) -eq 'tag'");
        Assert.True(at >= 0);
        Assert.True(IndexOf(code, "::notice::", at) > at);
        Assert.DoesNotContain("(git cat-file -t $tag) -ne 'tag'", CodeText("testing-release.yml"));
    }

    [Fact] // [EDGE-plugin-release-4]
    public void TestingRelease_rejects_a_tag_outside_the_mirror_format()
    {
        var code = Code("testing-release.yml");
        var check = IndexOf(code, "'^testing-[0-9]{1,10}$'");
        var firstDotnet = code.FindIndex(l => Regex.IsMatch(l, @"\bdotnet\s"));
        Assert.True(check >= 0 && firstDotnet >= 0 && check < firstDotnet, $"{check} {firstDotnet}");
        Assert.Contains("exit 1", code[check + 1] + code[check]);
    }

    [Fact] // [EDGE-plugin-release-5]
    public void Trigger_blocks_are_exactly_the_plugin_tags_and_branches()
    {
        Assert.Equal(["v*"], TagPatterns(OnBlock(Raw("release.yml"))));
        Assert.Equal(["testing-*"], TagPatterns(OnBlock(Raw("testing-release.yml"))));
        Assert.DoesNotContain(OnBlock(Raw("test.yml")), l => l.Trim() == "tags:");
        foreach (var name in AllWorkflows())
            Assert.DoesNotContain(OnBlock(Raw(name)), l => l.Contains("backdrops", StringComparison.OrdinalIgnoreCase));
    }

    [Theory] // [EDGE-plugin-release-6]
    [InlineData("release.yml")]
    [InlineData("testing-release.yml")]
    public void Notify_warns_and_succeeds_without_a_token(string name)
    {
        var code = Code(name);
        var at = IndexOf(code, "if (-not $env:PLUGINS_REPO_TOKEN)");
        Assert.True(at >= 0);
        var warn = IndexOf(code, "::warning::", at);
        var exit = code.FindIndex(at, l => l.Trim() == "exit 0");
        Assert.True(warn > at && exit > warn, $"{at} {warn} {exit}");
        Assert.True(exit < IndexOf(code, "Invoke-RestMethod", at));
    }

    [Fact] // [EDGE-plugin-release-7]
    public void Every_checkout_fetches_full_history()
    {
        var found = 0;
        foreach (var name in AllWorkflows())
        {
            var code = Code(name);
            for (var i = 0; i < code.Count; i++)
            {
                if (!code[i].Contains("uses: actions/checkout@")) continue;
                found++;
                var indent = code[i].Length - code[i].TrimStart().TrimStart('-').TrimStart().Length;
                var with = code.Skip(i + 1).TakeWhile(l => l.Trim().Length == 0 || l.Length - l.TrimStart().Length >= indent).ToList();
                Assert.True(with.Any(l => l.Trim() == "fetch-depth: 0"), $"{name}: checkout at line {i + 1} lacks fetch-depth: 0");
            }
        }
        Assert.True(found >= 3);
    }

    [Fact] // [EDGE-plugin-release-15]
    public void Only_release_workflows_can_write()
    {
        foreach (var name in AllWorkflows())
        {
            var text = CodeText(name);
            var writes = text.Contains("contents: write") || text.Contains("secrets.") || text.Contains("action-gh-release");
            Assert.True(writes == Releases.Contains(name), $"{name}: writes={writes}");
        }
    }

    [Theory] // [EDGE-plugin-release-16]
    [InlineData("release.yml")]
    [InlineData("testing-release.yml")]
    public void Changelog_heredoc_uses_a_random_delimiter(string name)
    {
        var text = CodeText(name);
        Assert.Contains("Changelog<<", text);
        Assert.Contains("NewGuid()", text);
        Assert.Contains("GITHUB_ENV", text);
    }

    [Fact] // [EDGE-plugin-release-17]
    public void Every_workflow_checks_the_Dalamud_api_level_first()
    {
        foreach (var name in AllWorkflows())
        {
            var code = Code(name);
            var check = IndexOf(code, "GetAssemblyName");
            var firstDotnet = code.FindIndex(l => Regex.IsMatch(l, @"\bdotnet\s"));
            Assert.True(check >= 0, $"{name}: no API-level check");
            Assert.True(firstDotnet > check, $"{name}: the API-level check must come before the first dotnet line");
            var text = CodeText(name);
            Assert.Contains("Dalamud.dll", text);
            Assert.Contains("DalamudApiLevel", text);
        }
    }

    private static readonly (string What, Regex Pattern)[] Disablers =
    [
        ("continue-on-error", new Regex("continue-on-error")),
        ("a step-level if:", new Regex(@"^\s+if:")),
        ("catch", new Regex(@"\bcatch\b")),
        ("trap", new Regex(@"\btrap\b")),
        ("|| true", new Regex(@"\|\|\s*true")),
        ("SilentlyContinue", new Regex("SilentlyContinue")),
        ("-ErrorAction", new Regex("-ErrorAction")),
        ("$ErrorActionPreference", new Regex(@"\$ErrorActionPreference")),
        ("-SkipHttpErrorCheck", new Regex("-SkipHttpErrorCheck")),
    ];

    [Fact] // [EDGE-plugin-release-18]
    public void No_guard_or_dispatch_can_be_silently_disabled()
    {
        foreach (var name in AllWorkflows())
            foreach (var line in Code(name))
                foreach (var (what, pattern) in Disablers)
                    Assert.False(pattern.IsMatch(line), $"{name}: {what} in '{line.Trim()}'");

        var script = File.ReadAllLines(Path.Combine(RepoPaths.Root(), "tools", "check-package.ps1"))
                         .Where(l => !l.TrimStart().StartsWith('#')).ToList();
        foreach (var line in script)
            foreach (var (what, pattern) in Disablers.Where(d => d.What is "catch" or "trap" or "SilentlyContinue" or "-ErrorAction" or "$ErrorActionPreference"))
                Assert.False(pattern.IsMatch(line), $"check-package.ps1: {what} in '{line.Trim()}'");
    }

    [Fact] // [EDGE-plugin-release-24]
    public void Native_commands_are_last_or_exit_checked()
    {
        var blocks = 0;
        foreach (var name in AllWorkflows())
            foreach (var block in RunBlocks(Raw(name)))
            {
                blocks++;
                var bad = UncheckedNativeCommands(block);
                Assert.True(bad.Count == 0, $"{name}: unchecked native command(s): {string.Join(" | ", bad)}");
            }
        Assert.True(blocks >= 10);
    }

    public static TheoryData<string, bool> NativeSamples => new()
    {
        { "Write-Host hi\ndotnet build x", true },
        { "dotnet test x\nWrite-Host done", false },
        { "git fetch --force --tags origin\n" + ExitCheck + "\nWrite-Host ok", true },
        { "$env:Changelog = ((git tag -l --format='%(contents)' $tag) -join \"`n\").Trim()\nWrite-Host x", false },
        { "if ((git cat-file -t $tag) -ne 'tag') {\n  throw \"no\"\n}\nWrite-Host x", true },
        { "& ./tools/check-package.ps1 -Zip latest.zip\nWrite-Host after", false },
    };

    [Theory] // [EDGE-plugin-release-24]
    [MemberData(nameof(NativeSamples))]
    public void Native_command_helper_classifies_samples(string block, bool passes) =>
        Assert.Equal(passes, UncheckedNativeCommands(block.Split('\n').ToList()).Count == 0);
}
