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
            Assert.True(source!.Source.Contains("VFXEditor") || source.Source.Contains("FFXIVClientStructs") || source.Source.Contains("Brio"),
                        f.Name);
        }
    }

    [Fact] // [AC-sun-off-5] [EDGE-sun-off-1]
    public void UpdateEnvironmentPresent()
    {
        Assert.Equal("0F 10 42 08 0F 11 41 08 F2 0F 10 4A 18", Signatures.UpdateEnvironment);
        var field = typeof(Signatures).GetField(nameof(Signatures.UpdateEnvironment))!;
        Assert.Contains("Brio", field.GetCustomAttribute<SourceAttribute>()!.Source);
    }
}
