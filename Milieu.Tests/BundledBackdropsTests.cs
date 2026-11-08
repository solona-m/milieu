using System.IO;
using System.Linq;
using Milieu.Assets;
using Milieu.Imaging;
using Milieu.Tests.Support;
using StbImageSharp;
using Xunit;

namespace Milieu.Tests;

/// <summary>
/// Product-asset tests: they read the committed images in Milieu/DefaultBackdrops (the acknowledged exception to the
/// synthetic-fixture rule, docs/adr/0004).
/// </summary>
public class BundledBackdropsTests
{
    [Fact] // [AC-hdri-backdrop-11]
    public void CommittedImages_ObeyLimitsAndShapes()
    {
        foreach (var pin in DefaultBackdropsDownloadService.Backdrops)
        {
            var path = Path.Combine(RepoPaths.DefaultBackdrops(), pin.Name);
            var head = File.ReadAllBytes(path).Take(3).ToArray();
            Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, head);
            var probe = ImageLoader.Probe(path);
            Assert.Equal((8192, 4096), (probe.Width, probe.Height));
            Assert.False(probe.Progressive, pin.Name);
            Assert.True(DecodeBudget.PredictPeakBytes(probe, 8192) < DecodeBudget.BudgetBytes);
        }
    }

    [Fact, Trait("Speed", "Slow")] // [AC-hdri-backdrop-11] [EDGE-hdri-backdrop-60]
    public void CommittedImages_WrapAround()
    {
        foreach (var pin in DefaultBackdropsDownloadService.Backdrops)
        {
            using var s = File.OpenRead(Path.Combine(RepoPaths.DefaultBackdrops(), pin.Name));
            var img = ImageResult.FromStream(s, ColorComponents.RedGreenBlue);
            Assert.False(SeamDetector.Measure(img.Data, img.Width, img.Height).NoWrap, pin.Name);
        }
    }

    [Fact] // [AC-hdri-backdrop-11]
    public void Credits_ListsEveryPinnedFile()
    {
        var credits = File.ReadAllText(Path.Combine(RepoPaths.DefaultBackdrops(), "CREDITS.md"));
        foreach (var pin in DefaultBackdropsDownloadService.Backdrops) Assert.Contains(pin.Name, credits);
    }

    [Fact] // [AC-hdri-backdrop-11]
    public void Csproj_DoesNotShipDefaultBackdrops()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoPaths.Root(), "Milieu", "Milieu.csproj"));
        Assert.Contains(@"<None Remove=""DefaultBackdrops\**"" />", csproj);
        Assert.Contains(@"<Content Remove=""DefaultBackdrops\**"" />", csproj);
        var output = Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!;
        Assert.Empty(Directory.GetFiles(output, "*.jpeg", SearchOption.AllDirectories));
    }

    [ReferenceFileFact(ReferenceFiles.HdriSourceDir)] // [EDGE-hdri-backdrop-52]
    public void Lodge_IsByteIdenticalToSource_WhenSourcePresent()
    {
        var src = Path.Combine(ReferenceFiles.Path(ReferenceFiles.HdriSourceDir), "lodge.jpeg");
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(Path.Combine(RepoPaths.DefaultBackdrops(), "lodge.jpeg")));
    }
}
