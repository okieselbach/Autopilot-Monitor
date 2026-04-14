using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Unit tests for the ServerAction queue serialization layer. The real round-trip through
/// Table Storage is covered by manual QA; these tests pin the pure logic that would otherwise
/// silently break on refactor.
/// </summary>
public class ServerActionQueueTests
{
    [Fact]
    public void DeserializePendingActions_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(TableStorageService.DeserializePendingActions(null));
        Assert.Empty(TableStorageService.DeserializePendingActions(string.Empty));
        Assert.Empty(TableStorageService.DeserializePendingActions("   "));
    }

    [Fact]
    public void DeserializePendingActions_Malformed_ReturnsEmpty_DoesNotThrow()
    {
        // Ingest must never fail because of a corrupt queue column. A swallowed exception here is
        // acceptable — telemetry on the delivery path will show the empty list.
        Assert.Empty(TableStorageService.DeserializePendingActions("not json"));
        Assert.Empty(TableStorageService.DeserializePendingActions("{broken"));
    }

    [Fact]
    public void DeserializePendingActions_ValidJson_RoundTrips()
    {
        var original = new List<ServerAction>
        {
            new()
            {
                Type = ServerActionTypes.TerminateSession,
                Reason = "Rule ANALYZE-ESP-002 fired (KO criterion)",
                RuleId = "ANALYZE-ESP-002",
                QueuedAt = new DateTime(2026, 4, 14, 10, 0, 0, DateTimeKind.Utc),
                Params = new Dictionary<string, string>
                {
                    { "gracePeriodSeconds", "30" },
                    { "uploadDiagnostics", "true" }
                }
            },
            new()
            {
                Type = ServerActionTypes.RotateConfig,
                Reason = "Admin updated gather rules",
                QueuedAt = new DateTime(2026, 4, 14, 10, 5, 0, DateTimeKind.Utc)
            }
        };

        var json = JsonConvert.SerializeObject(original);
        var parsed = TableStorageService.DeserializePendingActions(json);

        Assert.Equal(2, parsed.Count);
        Assert.Equal(ServerActionTypes.TerminateSession, parsed[0].Type);
        Assert.Equal("ANALYZE-ESP-002", parsed[0].RuleId);
        Assert.NotNull(parsed[0].Params);
        Assert.Equal("30", parsed[0].Params!["gracePeriodSeconds"]);
        Assert.Equal(ServerActionTypes.RotateConfig, parsed[1].Type);
        Assert.Null(parsed[1].RuleId);
    }

    [Fact]
    public void ServerActionTypes_AreStableStrings()
    {
        // The agent matches on these strings. Changing them breaks backward compat, so pin the
        // values explicitly — a rename must be a deliberate breaking change.
        Assert.Equal("terminate_session", ServerActionTypes.TerminateSession);
        Assert.Equal("rotate_config", ServerActionTypes.RotateConfig);
        Assert.Equal("request_diagnostics", ServerActionTypes.RequestDiagnostics);
    }
}
