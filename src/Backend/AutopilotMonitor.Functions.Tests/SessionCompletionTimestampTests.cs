using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Regression cover for the duration-inflation bug observed on session
/// fe5d7153-1e0c-4ff9-812b-2531ff77bed3 (DurationSeconds=1729 vs. real ~415s).
/// Root cause: admin-marked terminals + rule-engine + maintenance auto-fail used
/// <c>DateTime.UtcNow</c> as <c>CompletedAt</c> when the agent never sent an
/// enrollment_complete event. The dashboard list/avg consumed the inflated stored
/// duration; the session detail page silently masked the bug by recomputing from
/// events client-side.
/// </summary>
public class SessionCompletionTimestampTests
{
    private static readonly DateTime LastEvent = new(2026, 4, 29, 10, 59, 30, DateTimeKind.Utc);
    private static readonly DateTime Now       = new(2026, 4, 29, 11, 21, 27, DateTimeKind.Utc);
    private static readonly DateTime AgentEvt  = new(2026, 4, 29, 10, 58, 16, DateTimeKind.Utc);

    [Fact]
    public void ResolveCompletionTimestamp_prefers_caller_supplied_completion_event()
    {
        // Agent ingest path: CompletionEvent.Timestamp is authoritative — never overridden.
        var resolved = TableStorageService.ResolveCompletionTimestamp(AgentEvt, LastEvent, Now);
        Assert.Equal(AgentEvt, resolved);
    }

    [Fact]
    public void ResolveCompletionTimestamp_falls_back_to_LastEventAt_when_completedAt_null()
    {
        // Admin "Mark as Succeeded" / rule-engine / maintenance auto-fail paths pass
        // completedAt: null. Without this fallback, DurationSeconds would inflate to
        // (button-click − earliestEvent) instead of (lastEvent − earliestEvent).
        var resolved = TableStorageService.ResolveCompletionTimestamp(null, LastEvent, Now);
        Assert.Equal(LastEvent, resolved);
    }

    [Fact]
    public void ResolveCompletionTimestamp_falls_back_to_now_when_session_has_no_events()
    {
        // Pathological case: terminal flip on a session that never emitted any event.
        // No better anchor exists, so wall-clock is the sane last-resort.
        var resolved = TableStorageService.ResolveCompletionTimestamp(null, null, Now);
        Assert.Equal(Now, resolved);
    }

    [Fact]
    public void ResolveCompletionTimestamp_does_not_override_explicit_completedAt_with_lastEvent()
    {
        // Even if LastEventAt > completedAt (out-of-order ingest), explicit completedAt
        // wins. Reordering would silently shift DurationSeconds without a code change.
        var late = LastEvent.AddMinutes(5);
        var resolved = TableStorageService.ResolveCompletionTimestamp(AgentEvt, late, Now);
        Assert.Equal(AgentEvt, resolved);
    }
}
