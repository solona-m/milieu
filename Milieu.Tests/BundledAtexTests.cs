using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Milieu.Assets;
using Milieu.Backdrop;
using Milieu.Imaging;
using Milieu.Tests.Support;
using StbImageSharp;
using Xunit;

namespace Milieu.Tests;

/// <summary>
/// The pre-converted bundled textures are not committed (docs/adr/0005): these tests read the PrepareBackdrops output
/// when an environment variable points at it, and skip otherwise.
/// </summary>
public class BundledAtexTests
{
    public const string AtexDirVar = "MILIEU_BACKDROP_ATEX_DIR";
    public const string VerifyVar = "MILIEU_VERIFY_ATEX";

    [ReferenceFileFact(AtexDirVar)] // [AC-hdri-backdrop-11]
    public void Reference_PinnedAtexMatchesArtifacts()
    {
        var problems = BackdropPins.Verify(ReferenceFiles.Path(AtexDirVar), BackdropPins.Tag);
        Assert.Empty(problems);
        Assert.Equal(8, Directory.GetFiles(ReferenceFiles.Path(AtexDirVar), "*.atex").Length);
    }

    [Fact, Trait("Speed", "Slow")] // [EDGE-hdri-backdrop-68]
    public async Task RegeneratedFromCommittedJpegs_MatchesPins()
    {
        if (Environment.GetEnvironmentVariable(VerifyVar) != "1") return;   // opt-in: several minutes of BC7
        foreach (var pin in BackdropPins.All)
        {
            ImageResult img;
            using (var s = File.OpenRead(Path.Combine(RepoPaths.DefaultBackdrops(), pin.Stem + ".jpeg"))) img = ImageResult.FromStream(s, ColorComponents.RedGreenBlue);
            using var ms = new MemoryStream();
            await TextureConversion.ConvertToAtexAsync(img.Data, img.Width, img.Height, 8192, 4096, new Bc7(), ms);
            Assert.Equal(pin.Bytes, ms.Length);
            ms.Position = 0;
            Assert.Equal(pin.Sha256, Convert.ToHexString(SHA256.HashData(ms)).ToLowerInvariant());
        }
    }
}
