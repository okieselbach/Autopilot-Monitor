using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins <see cref="MaintenanceService.BuildSessionTimeoutEvent"/> — the server-authored
/// <c>session_timeout</c> event the 5h maintenance sweep injects into the stream so the analyze
/// pipeline (ANALYZE-ENRL-002) can fire on an otherwise event-silent terminal timeout. A full
/// service smoke test would need ~17 deps; the load-bearing logic here is the field shape and the
/// Sequence assignment (must sort AFTER the session's last event so it isn't interleaved into the
/// canonical Sequence order), so that lives behind a pure static helper (analog to DecideAutoAction).
/// </summary>
public class MaintenanceServiceSessionTimeoutEventTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    private static SessionSummary Session() => new()
    {
        TenantId = TenantId,
        SessionId = SessionId,
        StartedAt = new DateTime(2026, 5, 31, 8, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void Sequence_IsOnePastTheSessionMax()
    {
        var existing = new List<EnrollmentEvent>
        {
            new() { Sequence = 3 },
            new() { Sequence = 41 }, // max — even though out of array order
            new() { Sequence = 17 },
        };
        var now = new DateTime(2026, 5, 31, 13, 0, 0, DateTimeKind.Utc);

        var evt = MaintenanceService.BuildSessionTimeoutEvent(Session(), 5, existing, now);

        Assert.Equal(42, evt.Sequence);
    }

    [Fact]
    public void Sequence_IsOne_WhenNoPriorEvents()
    {
        var now = new DateTime(2026, 5, 31, 13, 0, 0, DateTimeKind.Utc);

        Assert.Equal(1, MaintenanceService.BuildSessionTimeoutEvent(Session(), 5, new List<EnrollmentEvent>(), now).Sequence);
        Assert.Equal(1, MaintenanceService.BuildSessionTimeoutEvent(Session(), 5, null!, now).Sequence);
    }

    [Fact]
    public void Shape_IsTerminalServerAuthoredAndPhaseUnknown()
    {
        var now = new DateTime(2026, 5, 31, 13, 0, 0, DateTimeKind.Utc);

        var evt = MaintenanceService.BuildSessionTimeoutEvent(Session(), 5, new List<EnrollmentEvent>(), now);

        Assert.Equal("session_timeout", evt.EventType);
        Assert.Equal(AutopilotMonitor.Shared.Constants.EventTypes.SessionTimeout, evt.EventType);
        Assert.Equal("System.Maintenance", evt.Source);
        Assert.Equal(EventSeverity.Error, evt.Severity);
        // Non-phase event: must stay Unknown so the UI timeline doesn't render a phantom phase boundary.
        Assert.Equal(EnrollmentPhase.Unknown, evt.Phase);
        Assert.Equal(now, evt.Timestamp);
        Assert.Equal(TenantId, evt.TenantId);
        Assert.Equal(SessionId, evt.SessionId);
    }

    [Fact]
    public void Data_CarriesTimeoutAttribution()
    {
        var now = new DateTime(2026, 5, 31, 13, 0, 0, DateTimeKind.Utc);

        var evt = MaintenanceService.BuildSessionTimeoutEvent(Session(), 5, new List<EnrollmentEvent>(), now);

        Assert.Equal(5, evt.Data["timeoutHours"]);
        Assert.Equal("maintenance_sweep", evt.Data["source"]);
        Assert.Contains("2026-05-31", evt.Data["startedAt"].ToString());
    }
}
