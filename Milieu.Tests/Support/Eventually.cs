using System;
using System.Diagnostics;
using System.Threading;

namespace Milieu.Tests.Support;

/// <summary>
/// Waits for background work in tests. Polls with a short sleep instead of spinning, so the waiting test thread
/// doesn't compete for CPU with the work it waits on (a 2-core CI runner running test classes in parallel starved
/// the jobs under a spinning wait). The timeout is generous because it bounds a hang, not a speed: a
/// passing test returns as soon as the condition holds.
/// </summary>
internal static class Eventually
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public static bool Holds(Func<bool> condition, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > limit) return condition();
            Thread.Sleep(10);
        }
        return true;
    }
}
