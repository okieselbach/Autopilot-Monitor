using AutopilotMonitor.Functions.Functions.Config;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="GetAgentConfigFunction.IsV2Client"/> — the pure decision logic that picks
/// the V1 vs V2 hash-oracle fields based on the X-Agent-Version header value.
/// </summary>
public class GetAgentConfigFunctionTests
{
    [Theory]
    // V2 agents (major >= 2) — must route to the V2 hash oracle.
    [InlineData("2.0.114", true)]
    [InlineData("2.0.0", true)]
    [InlineData("2.1.3", true)]
    [InlineData("2.0.114+06bbf13dbeedc", true)]  // SemVer build-metadata suffix
    [InlineData("3.0.0", true)]
    // V1 agents (major < 2) — must stay on the legacy hash oracle.
    [InlineData("1.0.1041", false)]
    [InlineData("1.5.0", false)]
    [InlineData("0.9.0", false)]
    // Backward compat — missing/unparsable headers default to V1 so existing agents keep working.
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("not-a-version", false)]
    [InlineData("vNEXT", false)]
    public void IsV2Client_MajorVersionGate(string? agentVersion, bool expectedV2)
    {
        var actual = GetAgentConfigFunction.IsV2Client(agentVersion);
        Assert.Equal(expectedV2, actual);
    }
}
