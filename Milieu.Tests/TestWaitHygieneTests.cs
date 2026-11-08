using System.IO;
using System.Linq;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

/// <summary>Waits on background work must not busy-spin (they starved the jobs on a 2-core CI runner).</summary>
public class TestWaitHygieneTests
{
    [Fact]
    public void Tests_wait_with_Eventually_not_SpinUntil()
    {
        var dir = Path.Combine(RepoPaths.Root(), "Milieu.Tests");
        var offenders = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.EndsWith(nameof(TestWaitHygieneTests) + ".cs"))
            .Where(f => File.ReadAllText(f).Contains("SpinWait" + ".SpinUntil"))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(offenders);
    }
}
