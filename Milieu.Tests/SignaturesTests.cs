using System.Linq;
using System.Reflection;
using Milieu.Interop;
using Xunit;

namespace Milieu.Tests;

public class SignaturesTests
{
    [Fact] // [EDGE-hdri-backdrop-4]
    public void AllSignaturesHaveSourceComments()
    {
        var fields = typeof(Signatures).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).ToList();
        Assert.NotEmpty(fields);
        foreach (var f in fields)
        {
            Assert.False(string.IsNullOrWhiteSpace((string)f.GetRawConstantValue()!), f.Name);
            var source = f.GetCustomAttribute<SourceAttribute>();
            Assert.NotNull(source);
            Assert.True(source!.Source.Contains("VFXEditor") || source.Source.Contains("FFXIVClientStructs"), f.Name);
        }
    }
}
