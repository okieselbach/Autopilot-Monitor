using AutopilotMonitor.Functions.Functions.Ingest;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for NdjsonParser — the NDJSON+gzip parsing contract.
/// If the wire format between agent and backend ever drifts, these tests will catch it.
/// </summary>
public class NdjsonParserTests
{
    private static readonly string ValidTenantId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private static readonly string ValidSessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    // --- ParseNdjson (plain string, no gzip) ---

    [Fact]
    public void ParseNdjson_MetadataOnly_ReturnsCorrectTenantAndSession()
    {
        var ndjson = JsonConvert.SerializeObject(new { SessionId = ValidSessionId, TenantId = ValidTenantId });

        var (sessionId, tenantId, events) = NdjsonParser.ParseNdjson(ndjson);

        Assert.Equal(ValidSessionId, sessionId);
        Assert.Equal(ValidTenantId, tenantId);
        Assert.Empty(events);
    }

    [Fact]
    public void ParseNdjson_WithEvents_ReturnsAllEvents()
    {
        var lines = new[]
        {
            JsonConvert.SerializeObject(new { SessionId = ValidSessionId, TenantId = ValidTenantId }),
            JsonConvert.SerializeObject(new EnrollmentEvent { EventType = "phase_changed", Message = "Device Setup" }),
            JsonConvert.SerializeObject(new EnrollmentEvent { EventType = "app_install_started", Message = "MyApp" }),
        };
        var ndjson = string.Join('\n', lines);

        var (_, _, events) = NdjsonParser.ParseNdjson(ndjson);

        Assert.Equal(2, events.Count);
        Assert.Equal("phase_changed", events[0].EventType);
        Assert.Equal("app_install_started", events[1].EventType);
    }

    [Fact]
    public void ParseNdjson_EventsDoNotInheritTenantIdFromMetadata()
    {
        // Critical contract: the parser does NOT stamp TenantId onto events.
        // That is done by StampServerFields — these are two separate responsibilities.
        var lines = new[]
        {
            JsonConvert.SerializeObject(new { SessionId = ValidSessionId, TenantId = ValidTenantId }),
            JsonConvert.SerializeObject(new EnrollmentEvent { EventType = "test_event" }),
        };
        var ndjson = string.Join('\n', lines);

        var (_, _, events) = NdjsonParser.ParseNdjson(ndjson);

        // Event has no TenantId from agent JSON → will be null until StampServerFields is called
        Assert.Null(events[0].TenantId);
        Assert.Null(events[0].SessionId);
    }

    [Fact]
    public void ParseNdjson_EventWithAgentSuppliedTenantId_PreservesItUntilStamped()
    {
        var agentTenantId = "c3d4e5f6-a7b8-9012-cdef-012345678901";
        var lines = new[]
        {
            JsonConvert.SerializeObject(new { SessionId = ValidSessionId, TenantId = ValidTenantId }),
            JsonConvert.SerializeObject(new EnrollmentEvent { EventType = "test_event", TenantId = agentTenantId }),
        };
        var ndjson = string.Join('\n', lines);

        var (_, _, events) = NdjsonParser.ParseNdjson(ndjson);

        // Parser preserves what the agent sent — StampServerFields will override it
        Assert.Equal(agentTenantId, events[0].TenantId);
    }

    [Fact]
    public void ParseNdjson_TrailingNewlines_AreIgnored()
    {
        var ndjson = JsonConvert.SerializeObject(new { SessionId = ValidSessionId, TenantId = ValidTenantId })
                     + "\n\n\n";

        var (sessionId, tenantId, events) = NdjsonParser.ParseNdjson(ndjson);

        Assert.Equal(ValidSessionId, sessionId);
        Assert.Empty(events);
    }

    [Fact]
    public void ParseNdjson_NullOrEmptyLine_IsSkipped()
    {
        var lines = new[]
        {
            JsonConvert.SerializeObject(new { SessionId = ValidSessionId, TenantId = ValidTenantId }),
            "",
            JsonConvert.SerializeObject(new EnrollmentEvent { EventType = "real_event" }),
        };
        // Note: Split('\n', RemoveEmptyEntries) already removes empty lines —
        // this test verifies the parser handles extra whitespace lines gracefully.
        var ndjson = string.Join('\n', lines);

        var (_, _, events) = NdjsonParser.ParseNdjson(ndjson);

        Assert.Single(events);
        Assert.Equal("real_event", events[0].EventType);
    }

    [Fact]
    public void ParseNdjson_EventDataWithNestedObjects_IsNormalized()
    {
        var lines = new[]
        {
            JsonConvert.SerializeObject(new { SessionId = ValidSessionId, TenantId = ValidTenantId }),
            """{"EventType":"hardware_spec","Data":{"cpuName":"Intel i7","cores":8}}""",
        };
        var ndjson = string.Join('\n', lines);

        var (_, _, events) = NdjsonParser.ParseNdjson(ndjson);

        // After normalization, values should be native .NET types, not JValue/JObject
        Assert.Single(events);
        var cpuName = events[0].Data["cpuName"];
        Assert.IsNotType<Newtonsoft.Json.Linq.JValue>(cpuName);
        Assert.Equal("Intel i7", cpuName?.ToString());
    }

    [Fact]
    public void ParseNdjson_EmptyString_ThrowsInvalidOperationException()
    {
        Assert.Throws<InvalidOperationException>(() => NdjsonParser.ParseNdjson(""));
    }

    // --- ParseGzipAsync ---

    [Fact]
    public async Task ParseGzipAsync_ValidPayload_ReturnsCorrectData()
    {
        var payload = NdjsonParser.BuildGzipPayload(ValidSessionId, ValidTenantId,
        [
            new EnrollmentEvent { EventType = "phase_changed" },
            new EnrollmentEvent { EventType = "app_install_started" },
        ]);

        using var stream = new MemoryStream(payload);
        var (sessionId, tenantId, events) = await NdjsonParser.ParseGzipAsync(stream, 5 * 1024 * 1024);

        Assert.Equal(ValidSessionId, sessionId);
        Assert.Equal(ValidTenantId, tenantId);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task ParseGzipAsync_PayloadExceedsMaxSize_Throws()
    {
        var payload = NdjsonParser.BuildGzipPayload(ValidSessionId, ValidTenantId,
            Enumerable.Range(0, 100).Select(_ => new EnrollmentEvent
            {
                EventType = "test",
                Message = new string('x', 1000)
            }));

        using var stream = new MemoryStream(payload);

        // Restrict to 1 byte — will throw immediately
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NdjsonParser.ParseGzipAsync(stream, maxSizeBytes: 1));
    }

    // --- BuildGzipPayload (test helper verification) ---

    [Fact]
    public async Task BuildGzipPayload_RoundTrip_PreservesMetadata()
    {
        var payload = NdjsonParser.BuildGzipPayload(ValidSessionId, ValidTenantId, []);

        using var stream = new MemoryStream(payload);
        var (sessionId, tenantId, events) = await NdjsonParser.ParseGzipAsync(stream, 5 * 1024 * 1024);

        Assert.Equal(ValidSessionId, sessionId);
        Assert.Equal(ValidTenantId, tenantId);
        Assert.Empty(events);
    }
}
