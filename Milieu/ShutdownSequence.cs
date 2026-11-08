using System;
using System.Threading.Tasks;

namespace Milieu;

/// <summary>
/// The plugin's dispose order, as a testable unit: (1) cancel conversions and wait a bounded time, (2) stop downloads,
/// (3) tear down on the framework thread with a timeout, (4) unhook and unsubscribe, (5) free the native strings, or
/// leak them on purpose when teardown timed out (the game may still read them).
/// </summary>
public static class ShutdownSequence
{
    public static readonly TimeSpan JobWait = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan TeardownWait = TimeSpan.FromSeconds(5);

    /// <param name="runTeardown">Runs the teardown on the framework thread; returns false if it did not finish within the wait.</param>
    /// <returns>True if teardown finished (strings freed), false if it timed out or failed (strings leaked).</returns>
    public static bool Run(Func<Task?> cancelJobs, Action disposeDownloads, Func<TimeSpan, bool> runTeardown, Action unhook,
                           Action freeStrings, Action leakStrings, ILogSink log, TimeSpan? jobWait = null, TimeSpan? teardownWait = null)
    {
        var running = cancelJobs();
        try { running?.Wait(jobWait ?? JobWait); }
        catch (AggregateException) { /* cancelled or failed: it has stopped either way */ }

        disposeDownloads();

        bool tornDown;
        try { tornDown = runTeardown(teardownWait ?? TeardownWait); }
        catch (Exception e)
        {
            tornDown = false;
            log.Warning("[Milieu] Teardown failed: " + LogText.Describe(e));
        }
        if (!tornDown) log.Warning("[Milieu] Teardown did not finish in time (game exiting?); native strings are left allocated on purpose.");

        unhook();
        if (tornDown) freeStrings();
        else leakStrings();
        return tornDown;
    }
}
