using System.IO;
using System.Text;
using AutopilotMonitor.Functions.Functions.Ingest;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for the pure helpers on <see cref="IngestTelemetryFunction"/>. The HTTP-trigger end
/// of the function needs a live runtime harness to exercise; this file covers the deterministic
/// bits (PartitionKey parsing, body size-cap guard).
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

    // ============================================================ Payload size-cap guard

    [Fact]
    public async Task ReadBodyWithSizeCapAsync_accepts_body_under_cap()
    {
        var payload = Encoding.UTF8.GetBytes("[{\"kind\":\"Event\"}]");
        using var stream = new MemoryStream(payload);

        var (exceeded, body) = await IngestTelemetryFunction.ReadBodyWithSizeCapAsync(stream, maxBytes: 1024);

        Assert.False(exceeded);
        Assert.Equal("[{\"kind\":\"Event\"}]", body);
    }

    [Fact]
    public async Task ReadBodyWithSizeCapAsync_accepts_body_exactly_at_cap()
    {
        // Strict greater-than semantics (matches legacy NdjsonParser): equal-to is OK.
        var payload = new byte[100];
        using var stream = new MemoryStream(payload);

        var (exceeded, body) = await IngestTelemetryFunction.ReadBodyWithSizeCapAsync(stream, maxBytes: 100);

        Assert.False(exceeded);
        Assert.Equal(100, Encoding.UTF8.GetByteCount(body));
    }

    [Fact]
    public async Task ReadBodyWithSizeCapAsync_rejects_body_over_cap_without_draining_full_stream()
    {
        // 1 MB payload with a 100-byte cap → short-circuits after a single buffer read.
        var payload = new byte[1_000_000];
        using var stream = new MemoryStream(payload);

        var (exceeded, body) = await IngestTelemetryFunction.ReadBodyWithSizeCapAsync(stream, maxBytes: 100);

        Assert.True(exceeded);
        Assert.Equal(string.Empty, body);
        // Stream position advanced past the cap but not to EOF — the helper bails early to
        // bound memory on hostile senders.
        Assert.True(stream.Position < stream.Length);
    }

    [Fact]
    public async Task ReadBodyWithSizeCapAsync_empty_stream_returns_empty_body()
    {
        using var stream = new MemoryStream(Array.Empty<byte>());

        var (exceeded, body) = await IngestTelemetryFunction.ReadBodyWithSizeCapAsync(stream, maxBytes: 1024);

        Assert.False(exceeded);
        Assert.Equal(string.Empty, body);
    }
}
