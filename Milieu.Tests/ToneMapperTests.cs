using Milieu.Imaging;
using Xunit;

namespace Milieu.Tests;

public class ToneMapperTests
{
    [Fact] // [AC-hdri-backdrop-5]
    public void Exposure_DoublesPerStop()
    {
        var plus1 = ToneMapper.ToSrgb8([0.25f, 0.25f, 0.25f], 1, 1, 1f, ToneMapOperator.None);
        Assert.Equal(ToneMapper.ToByte(ToneMapper.SrgbEncode(0.5f)), plus1[0]);
        var minus1 = ToneMapper.ToSrgb8([1f, 1f, 1f], 1, 1, -1f, ToneMapOperator.None);
        Assert.Equal(ToneMapper.ToByte(ToneMapper.SrgbEncode(0.5f)), minus1[0]);
    }

    [Fact] // [AC-hdri-backdrop-5]
    public void Operators_KnownValues()
    {
        Assert.Equal(0.5f, ToneMapper.Operator(1f, ToneMapOperator.Reinhard), 5);
        Assert.Equal(0f, ToneMapper.Operator(0f, ToneMapOperator.Aces), 5);
        Assert.True(ToneMapper.Operator(1e6f, ToneMapOperator.Aces) <= 1f);
        Assert.Equal(1f, ToneMapper.Operator(5f, ToneMapOperator.None));
        foreach (var op in new[] { ToneMapOperator.None, ToneMapOperator.Reinhard, ToneMapOperator.Aces })
        {
            var prev = -1f;
            for (var x = 0f; x <= 100f; x += 0.25f)
            {
                var v = ToneMapper.Operator(x, op);
                Assert.True(v >= prev, $"{op} not monotonic at {x}");
                prev = v;
            }
        }
    }

    [Fact] // [AC-hdri-backdrop-5]
    public void SrgbEncoding_KnownValues()
    {
        Assert.Equal(0, ToneMapper.ToByte(ToneMapper.SrgbEncode(0f)));
        Assert.Equal(255, ToneMapper.ToByte(ToneMapper.SrgbEncode(1f)));
        Assert.Equal(188, ToneMapper.ToByte(ToneMapper.SrgbEncode(0.5f)));
        Assert.InRange(ToneMapper.ToByte(ToneMapper.SrgbEncode(0.0031308f)), 9, 11);
    }

    [Fact] // [EDGE-hdri-backdrop-12]
    public void NonFinite_Sanitised()
    {
        var px = ToneMapper.ToSrgb8([float.NaN, -1f, float.PositiveInfinity], 1, 1, 0, ToneMapOperator.Aces);
        Assert.Equal(0, px[0]);
        Assert.Equal(0, px[1]);
        Assert.Equal(255, px[2]);
        var none = ToneMapper.ToSrgb8([float.PositiveInfinity, float.NegativeInfinity, 0.5f], 1, 1, float.NaN, ToneMapOperator.None);
        Assert.Equal(255, none[0]);
        Assert.Equal(0, none[1]);
        Assert.Equal(188, none[2]);
    }
}
