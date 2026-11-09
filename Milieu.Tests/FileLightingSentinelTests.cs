using System.IO;
using Milieu.Lighting;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public class FileLightingSentinelTests
{
    [Fact] // [EDGE-scene-lighting-34]
    public void SetClearWasSet_RoundTrip()
    {
        using var t = new TempDir();
        var path = t.File(FileLightingSentinel.FileName);
        var first = new FileLightingSentinel(path, new FakeLog());
        Assert.False(first.WasSetAtStartup);
        first.Set();
        Assert.True(File.Exists(path));
        Assert.False(first.WasSetAtStartup);                       // read once, at construction
        Assert.True(new FileLightingSentinel(path, new FakeLog()).WasSetAtStartup);   // the next session sees it
        first.Clear();
        Assert.False(File.Exists(path));
        Assert.False(new FileLightingSentinel(path, new FakeLog()).WasSetAtStartup);
        first.Clear();                                             // clearing twice is fine
    }

    [Fact] // [EDGE-scene-lighting-34]
    public void IoErrorsSwallowed()
    {
        using var t = new TempDir();
        // A directory where the flag file should be: writing and deleting both fail.
        var asDir = t.File("flag-is-a-dir");
        Directory.CreateDirectory(asDir);
        var log = new FakeLog();
        var s = new FileLightingSentinel(asDir, log);
        Assert.False(s.WasSetAtStartup);
        s.Set();
        s.Clear();
        Assert.True(Directory.Exists(asDir));
        Assert.Contains(log.Lines, l => l.Level == "Debug" && l.Text.Contains("sentinel"));

        // A parent that doesn't exist.
        var missing = new FileLightingSentinel(Path.Combine(t.Path, "no", "such", "dir", "f.flag"), log);
        missing.Set();
        missing.Clear();
        Assert.False(missing.WasSetAtStartup);
    }
}
