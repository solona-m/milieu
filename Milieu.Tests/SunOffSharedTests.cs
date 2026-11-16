using System.Linq;
using System.Threading.Tasks;
using Milieu.Lighting;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

/// <summary>The state the detour and the framework thread share (docs/adr/0013).</summary>
public sealed unsafe class SunOffSharedTests
{
    [Fact] // [EDGE-sun-off-11]
    public void Close_StopsWritesForGood()
    {
        using var buf = new EnvBuffer();
        var s = new SunOffShared();
        s.SetWriting(true);
        buf.SetColours(0.8f, 0.7f, 0.6f, 0f, 0f, 0f);
        buf.Post(s);
        s.Approve();
        Assert.Equal(SunOffProbe.Passed, s.Probe);
        s.Close();
        Assert.False(s.Writing);
        s.SetWriting(true);
        Assert.False(s.Writing);
        var before = buf.Snapshot();
        Assert.Equal(PostResult.NotWriting, buf.Post(s));
        Assert.Equal(before, buf.Snapshot());
    }

    [Theory] // [EDGE-sun-off-23]
    [InlineData("State", 1, 0, 0, 0)]
    [InlineData("GPose", 0, 1, 0, 0)]
    [InlineData("Other", 0, 0, 1, 0)]
    [InlineData("A1Null", 0, 0, 1, 0)]
    [InlineData("EnvNull", 0, 0, 0, 1)]
    public void Classify(string how, long state, long gpose, long other, long noManager)
    {
        using var buf = new EnvBuffer();
        var s = new SunOffShared();
        switch (how)
        {
            case "State": SunOffPostStep.Run(buf.Env + 0x58, buf.Env, s); break;
            case "GPose": SunOffPostStep.Run(buf.Env + 0x448, buf.Env, s); break;
            case "Other": SunOffPostStep.Run(buf.Env + 0x28, buf.Env, s); break;
            case "A1Null": SunOffPostStep.Run(null, buf.Env, s); break;
            default: SunOffPostStep.Run(buf.State, null, s); break;
        }
        Assert.Equal(new SunOffCounts(1, state, gpose, other, noManager), s.Counts);
    }

    [Fact] // [EDGE-sun-off-24]
    public void DeltaSlots_FirstFourDistinct()
    {
        var s = new SunOffShared();
        foreach (var d in new long[] { 0x58, 0x58, 0x448, 0x28, -0x10, 0x999 }) s.RecordDelta(d);
        Assert.Equal(new long[] { 0x58, 0x448, 0x28, -0x10 }, s.Deltas());
    }

    [Fact] // [EDGE-sun-off-24]
    public void DeltaSlots_Concurrent()
    {
        for (var round = 0; round < 50; round++)
        {
            var s = new SunOffShared();
            Parallel.For(0, 64, i => s.RecordDelta(0x10 * (i % 8 + 1)));
            var d = s.Deltas();
            Assert.Equal(4, d.Count);
            Assert.Equal(4, d.Distinct().Count());
        }
    }

    [Fact] // [EDGE-sun-off-27]
    public void ProbeCas_OnlyOneCapture()
    {
        using var buf = new EnvBuffer();
        buf.SetColours(0.8f, 0.7f, 0.6f, 0f, 0f, 0f);
        var s = new SunOffShared();
        s.SetWriting(true);
        var results = new PostResult[200];
        Parallel.For(0, results.Length, i => results[i] = buf.Post(s));
        Assert.Equal(1, results.Count(r => r == PostResult.Probed));
        Assert.All(results.Where(r => r != PostResult.Probed), r => Assert.Equal(PostResult.AwaitingCheck, r));
        Assert.Equal(SunOffProbe.Captured, s.Probe);
        var six = new float[6];
        Assert.True(s.TryReadSample(six));
        Assert.Equal(new[] { 0.8f, 0.7f, 0.6f, 0f, 0f, 0f }, six);
    }

    [Fact] // [EDGE-sun-off-29]
    public void Rearm_OnlyFromCaptured()
    {
        using var buf = new EnvBuffer();
        buf.SetColours(0f, 0f, 0f, 0f, 0f, 0f);
        var s = new SunOffShared();
        Assert.False(s.Rearm());                          // NotProbed
        s.SetWriting(true);
        buf.Post(s);
        Assert.True(s.Rearm());                           // Captured → NotProbed
        Assert.Equal(SunOffProbe.NotProbed, s.Probe);
        buf.Post(s);
        s.Approve();
        Assert.False(s.Rearm());                          // Passed stays
        Assert.Equal(SunOffProbe.Passed, s.Probe);
        var failed = new SunOffShared();
        failed.SetWriting(true);
        buf.Post(failed);
        failed.Reject();
        Assert.False(failed.Rearm());
        Assert.Equal(SunOffProbe.Failed, failed.Probe);
        Assert.False(failed.Writing);
    }
}
