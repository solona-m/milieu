using System;
using System.Runtime.InteropServices;
using Milieu.Interop;
using Milieu.Lighting;

namespace Milieu.Tests.Support;

/// <summary>
/// A fake EnvManager in native memory for sun off's post-step (docs/adr/0013): <see cref="Env"/> is the manager,
/// <see cref="State"/> its own EnvState at +0x58. Filled with 0xAA so untouched bytes are easy to check.
/// </summary>
public sealed unsafe class EnvBuffer : IDisposable
{
    public const int Size = 0x500;

    public byte* Env { get; }

    public byte* State => Env + SunOffLayout.EnvManagerStateOffset;

    public EnvBuffer(byte fill = 0xAA)
    {
        Env = (byte*)NativeMemory.Alloc(Size);
        new Span<byte>(Env, Size).Fill(fill);
    }

    /// <summary>Writes the six sun/moon floats of the own state.</summary>
    public void SetColours(params float[] six)
    {
        var f = (float*)(State + SunOffLayout.SunlightColorOffset);
        for (var i = 0; i < six.Length; i++) f[i] = six[i];
    }

    public byte[] Snapshot() => new Span<byte>(Env, Size).ToArray();

    /// <summary>One call of the game's update on the own state.</summary>
    public PostResult Post(SunOffShared shared) => SunOffPostStep.Run(State, Env, shared);

    public void Dispose() => NativeMemory.Free(Env);
}
