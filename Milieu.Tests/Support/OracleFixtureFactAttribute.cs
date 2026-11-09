using Xunit;

namespace Milieu.Tests.Support;

/// <summary>A fact over the official-OpenEXR fixtures; skips (with the UNVERIFIED reason) when they were not generated.</summary>
public sealed class OracleFixtureFactAttribute : FactAttribute
{
    public const string SkipReason = "OpenEXR oracle fixtures were not generated; RLE/ZIP/PIZ unverified against the official library.";

    public OracleFixtureFactAttribute()
    {
        if (!ExrOracleFixtures.Available) Skip = SkipReason;
    }
}

/// <summary>A theory over the official-OpenEXR fixtures; skips when they were not generated.</summary>
public sealed class OracleFixtureTheoryAttribute : TheoryAttribute
{
    public OracleFixtureTheoryAttribute()
    {
        if (!ExrOracleFixtures.Available) Skip = OracleFixtureFactAttribute.SkipReason;
    }
}
