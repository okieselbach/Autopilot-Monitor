using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Tests;

public class BackendBuildInfoTests
{
    [Fact]
    public void Parse_VersionWithLongSha_TrimsTo7Chars()
    {
        var (version, commit) = BackendBuildInfo.ParseInformationalVersion(
            "1.0.0+2e82dd2bdeadbeefcafe1234567890abcdef1234");

        Assert.Equal("1.0.0", version);
        Assert.Equal("2e82dd2", commit);
    }

    [Fact]
    public void Parse_VersionWithoutSha_ReturnsEmptyCommit()
    {
        var (version, commit) = BackendBuildInfo.ParseInformationalVersion("1.0.0");

        Assert.Equal("1.0.0", version);
        Assert.Equal(string.Empty, commit);
    }

    [Fact]
    public void Parse_ShortShaBelowSeven_PreservedAsIs()
    {
        var (version, commit) = BackendBuildInfo.ParseInformationalVersion("1.0.0+abc12");

        Assert.Equal("1.0.0", version);
        Assert.Equal("abc12", commit);
    }

    [Fact]
    public void Parse_Empty_ReturnsFallback()
    {
        var (version, commit) = BackendBuildInfo.ParseInformationalVersion("");

        Assert.Equal("0.0.0", version);
        Assert.Equal(string.Empty, commit);
    }

    [Fact]
    public void Parse_NullSafe()
    {
        var (version, commit) = BackendBuildInfo.ParseInformationalVersion(null!);

        Assert.Equal("0.0.0", version);
        Assert.Equal(string.Empty, commit);
    }

    [Fact]
    public void Constructor_ReadsCurrentAssembly_ProducesNonEmptyVersion()
    {
        var info = new BackendBuildInfo();

        // Version comes from <Version> in .csproj via generated AssemblyInformationalVersion.
        // Test asserts only that the parser wired to the assembly produces something — we
        // don't hardcode the version string here to avoid coupling tests to every bump.
        Assert.False(string.IsNullOrWhiteSpace(info.Version));
        Assert.NotEqual(default, info.BuildUtc);
    }
}
