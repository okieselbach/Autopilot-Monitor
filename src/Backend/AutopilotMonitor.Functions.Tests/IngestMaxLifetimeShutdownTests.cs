using AutopilotMonitor.Functions.Functions.Ingest;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Session 8bc1180f (2026-06-12): the V2 max-lifetime watchdog stops the agent permanently
/// WITHOUT emitting <c>enrollment_failed</c> ("notbremse, not a session verdict"), so the
/// <c>agent_shutting_down(reason=max_lifetime)</c> event is the last one the session ever
/// sends — without a backend mapping the session stays InProgress forever. These tests pin
/// the shared detection predicate used by both classification copies (legacy ingest +
/// EventIngestProcessor); the status write itself rides the existing
/// <c>UpdateSessionStatusAsync(Failed)</c> path as the lowest-priority status writer.
/// </summary>
public class IngestMaxLifetimeShutdownTests
{
    private static EnrollmentEvent Shutdown(string? reason, string eventType = "agent_shutting_down")
    {
        var evt = new EnrollmentEvent
        {
            EventType = eventType,
            Message = "Agent shutting down (reason=max_lifetime, outcome=TimedOut).",
        };
        if (reason != null)
        {
            evt.Data = new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["outcome"] = "TimedOut",
                ["uptimeMinutes"] = 360.2,
            };
        }
        return evt;
    }

    [Fact]
    public void MaxLifetimeReason_IsDetected()
    {
        Assert.True(IngestEventsFunction.IsMaxLifetimeAgentShutdown(Shutdown("max_lifetime")));
    }

    [Fact]
    public void MaxLifetimeReason_IsDetected_CaseInsensitively()
    {
        Assert.True(IngestEventsFunction.IsMaxLifetimeAgentShutdown(Shutdown("Max_Lifetime")));
    }

    [Theory]
    // The full V2 shutdown-reason taxonomy: decision_terminal follows a real terminal event
    // (enrollment_complete / enrollment_failed already drove the status); the gap paths say
    // nothing terminal about the enrollment.
    [InlineData("decision_terminal")]
    [InlineData("ctrl_c")]
    [InlineData("process_exit")]
    [InlineData("unhandled_exception")]
    [InlineData("runtime_host_exit")]
    public void OtherShutdownReasons_AreNotDetected(string reason)
    {
        Assert.False(IngestEventsFunction.IsMaxLifetimeAgentShutdown(Shutdown(reason)));
    }

    [Fact]
    public void MissingReasonData_IsNotDetected()
    {
        Assert.False(IngestEventsFunction.IsMaxLifetimeAgentShutdown(Shutdown(reason: null)));
    }

    [Fact]
    public void OtherEventTypes_AreNotDetected_EvenWithMaxLifetimeReason()
    {
        Assert.False(IngestEventsFunction.IsMaxLifetimeAgentShutdown(
            Shutdown("max_lifetime", eventType: "agent_shutdown")));
    }

    [Fact]
    public void NullEvent_IsNotDetected()
    {
        Assert.False(IngestEventsFunction.IsMaxLifetimeAgentShutdown(null));
    }
}
