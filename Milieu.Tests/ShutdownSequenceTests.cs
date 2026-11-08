using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public class ShutdownSequenceTests
{
    [Fact] // [EDGE-hdri-backdrop-29]
    public void TeardownTimeout_DoesNotHang_LeaksStrings()
    {
        var calls = new List<string>();
        var log = new FakeLog();
        var never = new TaskCompletionSource().Task;
        var sw = Stopwatch.StartNew();
        var ok = ShutdownSequence.Run(
            cancelJobs: () => { calls.Add("cancel"); return never; },
            disposeDownloads: () => calls.Add("downloads"),
            runTeardown: _ => { calls.Add("teardown"); return false; },   // the framework never ran it
            unhook: () => calls.Add("unhook"),
            freeStrings: () => calls.Add("free"),
            leakStrings: () => calls.Add("leak"),
            log: log,
            jobWait: TimeSpan.FromMilliseconds(50));
        Assert.False(ok);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { "cancel", "downloads", "teardown", "unhook", "leak" }, calls);
        Assert.Contains(log.Lines, l => l.Level == "Warning" && l.Text.Contains("left allocated on purpose"));
    }

    [Fact] // [AC-hdri-backdrop-7] [EDGE-hdri-backdrop-37]
    public void CleanTeardown_FreesStrings_InOrder()
    {
        var calls = new List<string>();
        var ok = ShutdownSequence.Run(() => { calls.Add("cancel"); return Task.CompletedTask; }, () => calls.Add("downloads"),
            _ => { calls.Add("teardown"); return true; }, () => calls.Add("unhook"), () => calls.Add("free"), () => calls.Add("leak"), new FakeLog());
        Assert.True(ok);
        Assert.Equal(new[] { "cancel", "downloads", "teardown", "unhook", "free" }, calls);

        // A teardown that throws is treated like a timeout: warn and leak.
        var thrown = new List<string>();
        var log = new FakeLog();
        Assert.False(ShutdownSequence.Run(() => null, () => { }, _ => throw new InvalidOperationException("x"),
            () => thrown.Add("unhook"), () => thrown.Add("free"), () => thrown.Add("leak"), log));
        Assert.Equal(new[] { "unhook", "leak" }, thrown);
        Assert.Contains(log.Lines, l => l.Text.Contains("Teardown failed"));
    }
}
