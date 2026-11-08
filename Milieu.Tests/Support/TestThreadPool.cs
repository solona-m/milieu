using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Milieu.Tests.Support;

/// <summary>
/// Test classes run in parallel and several block a thread while they wait on background jobs. On a 2-core CI runner
/// the pool's slow thread injection then delays those jobs by seconds. A higher floor removes that starvation; it
/// changes nothing about the code under test.
/// </summary>
internal static class TestThreadPool
{
    [ModuleInitializer]
    internal static void RaiseFloor()
    {
        ThreadPool.GetMinThreads(out var workers, out var io);
        var floor = Math.Max(32, Environment.ProcessorCount * 4);
        ThreadPool.SetMinThreads(Math.Max(workers, floor), Math.Max(io, floor));
    }
}
