using System.Collections.Generic;
using System.Runtime.InteropServices;
using Milieu.Interop;
using Xunit;

namespace Milieu.Tests;

public class NativeStringPoolTests
{
    [Fact] // [EDGE-hdri-backdrop-41]
    public void PoolOnce_PathFreedAfter10Frames_OrNextSpawn_LeakOnTimeout()
    {
        var allocated = new List<nint>();
        var freed = new List<nint>();
        var pool = new NativeStringPool(n => { var p = Marshal.AllocHGlobal(n); allocated.Add(p); return p; }, p => { freed.Add(p); Marshal.FreeHGlobal(p); });

        var poolPtr = pool.Pool;
        Assert.Equal(poolPtr, pool.Pool);                     // allocated once
        Assert.Equal(Signatures.StaticVfxPool, Marshal.PtrToStringAnsi(poolPtr));

        var path = pool.Allocate("vfx/milieu/0123/dome.avfx");
        Assert.Equal("vfx/milieu/0123/dome.avfx", Marshal.PtrToStringAnsi(path));
        pool.ScheduleFree(path);
        for (var i = 0; i < NativeStringPool.FreeDelayFrames - 1; i++) pool.Tick();
        Assert.DoesNotContain(path, freed);
        pool.Tick();
        Assert.Contains(path, freed);

        // The next spawn frees pending strings early.
        var second = pool.Allocate("b");
        pool.ScheduleFree(second);
        pool.FreePending();
        Assert.Contains(second, freed);

        // Clean unload frees everything, the pool included.
        var third = pool.Allocate("c");
        pool.FreeAll();
        Assert.Contains(third, freed);
        Assert.Contains(poolPtr, freed);
        Assert.Equal(0, pool.Outstanding);

        // Teardown timeout: leak on purpose, free nothing.
        var leaky = new NativeStringPool(Marshal.AllocHGlobal, p => freed.Add(p));
        var before = freed.Count;
        _ = leaky.Pool;
        leaky.ScheduleFree(leaky.Allocate("d"));
        leaky.Leak();
        for (var i = 0; i < 20; i++) leaky.Tick();
        leaky.FreeAll();
        Assert.Equal(before, freed.Count);
    }
}
