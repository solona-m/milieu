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
    private static byte[] Source() => SyntheticImages.Rgb(256, 128, (x, y) => ((byte)(x * 3), (byte)(y * 2), (byte)((x ^ y) & 255)));

    [Fact] // [AC-hdri-backdrop-6]
    public async Task ConvertToAtex_HeaderEqualsLayout()
    {
        using var ms = new MemoryStream();
        byte[]? thumb = null;
        await TextureConversion.ConvertToAtexAsync(Source(), 256, 128, 1024, 512, new Bc7(), ms, onThumb: t => thumb = t);
        Assert.Equal(AtexWriter.Layout(1024, 512).TotalLength, ms.Length);
        Assert.Equal(AtexWriter.ExpectedHeader(1024, 512), ms.ToArray()[..80]);
        Assert.NotNull(thumb);
        Assert.Equal(256 * 128 * 4, thumb!.Length);
    }

    [Fact] // [EDGE-hdri-backdrop-68]
    public async Task Deterministic_ConvertToAtexTwice()
    {
        using var a = new MemoryStream();
        using var b = new MemoryStream();
        await TextureConversion.ConvertToAtexAsync(Source(), 256, 128, 1024, 512, new Bc7(), a);
        await TextureConversion.ConvertToAtexAsync(Source(), 256, 128, 1024, 512, new Bc7(), b);
        Assert.Equal(a.ToArray(), b.ToArray());
    }

    [Fact] // [EDGE-hdri-backdrop-69]
    public async Task PreparerAndToolPathProduceIdenticalAtex()
    {
        using var t = new TempDir();
        var png = SyntheticImages.WritePng(t.Path, "src.png", 256, 128, Source());
        var cache = new CacheStore(t.File("cache"));
        var prepared = await new BackdropPreparer(cache, new Bc7()).PrepareTextureAsync(new BackdropRequest(png, 8192, 0, ToneMapOperator.Aces), null, default);
        using var tool = new MemoryStream();
        await TextureConversion.ConvertToAtexAsync(Source(), 256, 128, prepared.Width, prepared.Height, new Bc7(), tool);
        Assert.Equal(tool.ToArray(), File.ReadAllBytes(prepared.DiskPath));
    }
}
