using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Milieu.Interop;

/// <summary>
/// Owns the NUL-terminated native strings handed to the game. The pool name is allocated once for the plugin's
/// lifetime. A path string is freed only some frames after its VFX was removed (removal may complete later), or when
/// the plugin unloads. On the teardown-timeout path everything is leaked on purpose: freeing memory the game might
/// still read is worse than a few leaked bytes at exit.
/// </summary>
public sealed class NativeStringPool(Func<int, nint>? alloc = null, Action<nint>? free = null)
{
    public const int FreeDelayFrames = 10;

    private readonly Func<int, nint> alloc = alloc ?? Marshal.AllocHGlobal;
    private readonly Action<nint> free = free ?? Marshal.FreeHGlobal;
    private readonly List<(nint Ptr, long DueFrame)> pending = [];
    private readonly HashSet<nint> live = [];
    private nint pool;
    private long frame;

    public int Outstanding => live.Count + (pool != 0 ? 1 : 0);

    public nint Pool => pool != 0 ? pool : pool = Allocate(Signatures.StaticVfxPool, track: false);

    public nint Allocate(string text) => Allocate(text, track: true);

    private nint Allocate(string text, bool track)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        var p = alloc(bytes.Length + 1);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        Marshal.WriteByte(p, bytes.Length, 0);
        if (track) live.Add(p);
        return p;
    }

    /// <summary>Frees the string <see cref="FreeDelayFrames"/> frames from now.</summary>
    public void ScheduleFree(nint p)
    {
        if (p == 0 || !live.Contains(p)) return;
        pending.Add((p, frame + FreeDelayFrames));
    }

    /// <summary>Advances one frame and frees what is due.</summary>
    public void Tick()
    {
        frame++;
        for (var i = pending.Count - 1; i >= 0; i--)
        {
            if (pending[i].DueFrame > frame) continue;
            FreeNow(pending[i].Ptr);
            pending.RemoveAt(i);
        }
    }

    /// <summary>Frees every pending path string now (the next spawn, or a clean unload after the VFX are gone).</summary>
    public void FreePending()
    {
        foreach (var (p, _) in pending) FreeNow(p);
        pending.Clear();
    }

    /// <summary>Clean unload: frees everything, the pool included.</summary>
    public void FreeAll()
    {
        FreePending();
        foreach (var p in live) free(p);
        live.Clear();
        if (pool != 0) { free(pool); pool = 0; }
    }

    /// <summary>Teardown timed out: forget everything without freeing.</summary>
    public void Leak()
    {
        pending.Clear();
        live.Clear();
        pool = 0;
    }

    private void FreeNow(nint p)
    {
        if (live.Remove(p)) free(p);
    }
}
