using AutopilotMonitor.Shared.Models.Deletion;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pure-logic tests for the cascade-delete state-machine constants + transition validator
/// (Plan §1 P7). The state-machine is the contract every cascade-related write hangs off:
/// the guard checks <see cref="SessionDeletionState.IsLocked"/>, the producer drives
/// <see cref="SessionDeletionState.IsValidTransition"/> via CAS, and the worker (PR4) follows
/// the same edges.
/// </summary>
public class SessionDeletionStateTests
{
    [Theory]
    [InlineData(SessionDeletionState.None,      false)]
    [InlineData(SessionDeletionState.Preparing, true)]
    [InlineData(SessionDeletionState.Queued,    true)]
    [InlineData(SessionDeletionState.Running,   true)]
    [InlineData(SessionDeletionState.Poisoned,  true)]
    [InlineData("",                              false)]
    [InlineData(null,                            false)]
    public void IsLocked_returns_true_for_every_non_None_lock_state(string? state, bool expected)
    {
        Assert.Equal(expected, SessionDeletionState.IsLocked(state));
    }

    [Theory]
    // Valid transitions per Plan §1 P7 + §16-R2 (Completed deliberately omitted — row absence is the success signal).
    [InlineData(SessionDeletionState.None,      SessionDeletionState.Preparing, true)]
    [InlineData(SessionDeletionState.Preparing, SessionDeletionState.Queued,    true)]
    [InlineData(SessionDeletionState.Preparing, SessionDeletionState.None,      true)]   // GC after 1h with no progress blob
    [InlineData(SessionDeletionState.Preparing, SessionDeletionState.Poisoned,  true)]   // producer crash + max-dequeue
    [InlineData(SessionDeletionState.Queued,    SessionDeletionState.Running,   true)]
    [InlineData(SessionDeletionState.Queued,    SessionDeletionState.Poisoned,  true)]
    [InlineData(SessionDeletionState.Running,   SessionDeletionState.Poisoned,  true)]
    [InlineData(SessionDeletionState.Poisoned,  SessionDeletionState.None,      true)]   // operator restore
    public void IsValidTransition_accepts_legal_edges(string from, string to, bool expected)
    {
        Assert.Equal(expected, SessionDeletionState.IsValidTransition(from, to));
    }

    [Theory]
    [InlineData(SessionDeletionState.None,      SessionDeletionState.Queued)]    // skip Preparing
    [InlineData(SessionDeletionState.None,      SessionDeletionState.Running)]
    [InlineData(SessionDeletionState.None,      SessionDeletionState.Poisoned)]
    [InlineData(SessionDeletionState.Queued,    SessionDeletionState.None)]      // never auto-clear Queued
    [InlineData(SessionDeletionState.Running,   SessionDeletionState.None)]      // never auto-clear Running
    [InlineData(SessionDeletionState.Running,   SessionDeletionState.Queued)]    // can't go backwards
    [InlineData(SessionDeletionState.Poisoned,  SessionDeletionState.Queued)]    // restore must go via None
    public void IsValidTransition_rejects_illegal_edges(string from, string to)
    {
        Assert.False(SessionDeletionState.IsValidTransition(from, to));
    }

    [Fact]
    public void IsValidTransition_treats_null_or_empty_from_as_None()
    {
        // Legacy rows pre-PR3 have no DeletionState column → null/empty must behave like None.
        Assert.True(SessionDeletionState.IsValidTransition(null, SessionDeletionState.Preparing));
        Assert.True(SessionDeletionState.IsValidTransition(string.Empty, SessionDeletionState.Preparing));
        Assert.False(SessionDeletionState.IsValidTransition(null, SessionDeletionState.Queued));
    }

    [Fact]
    public void Completed_is_intentionally_not_a_state_value()
    {
        // Plan §16-R2: the cascade FINAL step removes the Sessions row, so "completed" is
        // observable via row-absence + progress.completedAt — NOT via a stable state value.
        // No legal transition leads to a "Completed" state.
        Assert.False(SessionDeletionState.IsValidTransition(SessionDeletionState.Running, "Completed"));
        Assert.False(SessionDeletionState.IsValidTransition(SessionDeletionState.Queued, "Completed"));
        Assert.False(SessionDeletionState.IsLocked("Completed"));
    }
}
