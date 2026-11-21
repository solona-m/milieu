using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Milieu.Backdrop;
using Milieu.GameFiles;
using Milieu.Imaging;
using Milieu.Tests.Support;
using Xunit;

namespace Milieu.Tests;

public class TextureConversionTests
{
    /// <summary>Both block encoders: the reference (BCnEncoder) and the in-repo one (docs/adr/0016).</summary>
    private static IBlockEncoder Encoder(string name) => name == "reference" ? new Bc7() : new OpaqueBc7();

    private static byte[] Source() => SyntheticImages.Rgb(256, 128, (x, y) => ((byte)(x * 3), (byte)(y * 2), (byte)((x ^ y) & 255)));

    [Theory] // [AC-hdri-backdrop-6] [AC-fast-bc7-3]
    [InlineData("reference")]
    [InlineData("opaque")]
    public async Task ConvertToAtex_HeaderEqualsLayout(string encoder)
    {
        using var ms = new MemoryStream();
        byte[]? thumb = null;
        await TextureConversion.ConvertToAtexAsync(Source(), 256, 128, 1024, 512, Encoder(encoder), ms, onThumb: t => thumb = t);
        Assert.Equal(AtexWriter.Layout(1024, 512).TotalLength, ms.Length);
        Assert.Equal(AtexWriter.ExpectedHeader(1024, 512), ms.ToArray()[..80]);
        Assert.NotNull(thumb);
        Assert.Equal(256 * 128 * 4, thumb!.Length);
    }

    [Theory] // [EDGE-hdri-backdrop-68] [AC-fast-bc7-7]
    [InlineData("reference")]
    [InlineData("opaque")]
    public async Task Deterministic_ConvertToAtexTwice(string encoder)
    {
        using var a = new MemoryStream();
        using var b = new MemoryStream();
        await TextureConversion.ConvertToAtexAsync(Source(), 256, 128, 1024, 512, Encoder(encoder), a);
        await TextureConversion.ConvertToAtexAsync(Source(), 256, 128, 1024, 512, Encoder(encoder), b);
        Assert.Equal(a.ToArray(), b.ToArray());
    }

    /// <summary>
    /// The shared pipeline: the preparer and <see cref="TextureConversion"/> give the same bytes for the same encoder. (That
    /// the bundled-backdrop tool keeps the reference encoder is BundledAtexTests.PrepareBackdropsAtex_UsesReferenceEncoder.)
    /// </summary>
    [Theory] // [EDGE-hdri-backdrop-69] [AC-fast-bc7-7]
    [InlineData("reference")]
    [InlineData("opaque")]
    public async Task PreparerAndConversionPipelineProduceIdenticalAtex(string encoder)
    {
        using var t = new TempDir();
        var png = SyntheticImages.WritePng(t.Path, "src.png", 256, 128, Source());
        var cache = new CacheStore(t.File("cache"));
        var prepared = await new BackdropPreparer(cache, Encoder(encoder)).PrepareTextureAsync(new BackdropRequest(png, 8192, 0, ToneMapOperator.Aces), null, default);
        using var tool = new MemoryStream();
        await TextureConversion.ConvertToAtexAsync(Source(), 256, 128, prepared.Width, prepared.Height, Encoder(encoder), tool);
        Assert.Equal(tool.ToArray(), File.ReadAllBytes(prepared.DiskPath));
    }
}
