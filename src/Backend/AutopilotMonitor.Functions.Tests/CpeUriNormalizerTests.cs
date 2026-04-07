using AutopilotMonitor.Functions.Services.Vulnerability;

namespace AutopilotMonitor.Functions.Tests;

public class CpeUriNormalizerTests
{
    [Fact]
    public void Normalize_UrlEncodedPlus_LowerCase_BecomesBackslashQuoted()
    {
        Assert.Equal(
            @"cpe:2.3:a:microsoft:visual_c\+\+",
            CpeUriNormalizer.Normalize("cpe:2.3:a:microsoft:visual_c%2b%2b"));
    }

    [Fact]
    public void Normalize_UrlEncodedPlus_UpperCase_BecomesBackslashQuoted()
    {
        Assert.Equal(
            @"cpe:2.3:a:microsoft:visual_c\+\+",
            CpeUriNormalizer.Normalize("cpe:2.3:a:microsoft:visual_c%2B%2B"));
    }

    [Fact]
    public void Normalize_BarePlus_BecomesBackslashQuoted()
    {
        Assert.Equal(
            @"cpe:2.3:a:microsoft:visual_c\+\+",
            CpeUriNormalizer.Normalize("cpe:2.3:a:microsoft:visual_c++"));
    }

    [Fact]
    public void Normalize_AlreadyQuoted_IsIdempotent()
    {
        var canonical = @"cpe:2.3:a:microsoft:visual_c\+\+";
        Assert.Equal(canonical, CpeUriNormalizer.Normalize(canonical));
    }

    [Fact]
    public void Normalize_NoSpecialChars_PassesThrough()
    {
        Assert.Equal(
            "cpe:2.3:a:adobe:acrobat_dc",
            CpeUriNormalizer.Normalize("cpe:2.3:a:adobe:acrobat_dc"));
    }

    [Fact]
    public void Normalize_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal("", CpeUriNormalizer.Normalize(null));
        Assert.Equal("", CpeUriNormalizer.Normalize(""));
    }

    [Fact]
    public void Normalize_OtherPercentSequences_AreNotTouched()
    {
        // %20 (space), %2F (slash) etc. must NOT be rewritten — only %2B/%2b.
        Assert.Equal(
            "cpe:2.3:a:vendor:foo%20bar",
            CpeUriNormalizer.Normalize("cpe:2.3:a:vendor:foo%20bar"));
    }

    [Fact]
    public void Normalize_Wildcard_IsNotTouched()
    {
        // '*' is the CPE 2.3 ANY wildcard and must remain unquoted.
        Assert.Equal(
            "cpe:2.3:a:microsoft:windows:*:*",
            CpeUriNormalizer.Normalize("cpe:2.3:a:microsoft:windows:*:*"));
    }
}
