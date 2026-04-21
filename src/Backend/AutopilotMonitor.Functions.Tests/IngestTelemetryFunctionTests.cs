using AutopilotMonitor.Functions.Functions.Ingest;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for the pure helpers on <see cref="IngestTelemetryFunction"/>. The HTTP-trigger end
/// of the function needs a live runtime harness to exercise; this file covers the deterministic
/// bits (PartitionKey parsing, body/header TenantId reconciliation shape).
/// </summary>
public class IngestTelemetryFunctionTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    [Fact]
    public void TryParsePartitionKey_splits_tenant_and_session_on_single_underscore()
    {
        var ok = IngestTelemetryFunction.TryParsePartitionKey(
            $"{TenantId}_{SessionId}", out var tenant, out var session);

        Assert.True(ok);
        Assert.Equal(TenantId, tenant);
        Assert.Equal(SessionId, session);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("no-underscore-here")]
    [InlineData("_missing-tenant")]
    [InlineData("missing-session_")]
    [InlineData("too_many_parts_here")]
    public void TryParsePartitionKey_rejects_malformed_shapes(string? input)
    {
        var ok = IngestTelemetryFunction.TryParsePartitionKey(
            input!, out var tenant, out var session);

        Assert.False(ok);
        Assert.Equal(string.Empty, tenant);
        Assert.Equal(string.Empty, session);
    }
}
