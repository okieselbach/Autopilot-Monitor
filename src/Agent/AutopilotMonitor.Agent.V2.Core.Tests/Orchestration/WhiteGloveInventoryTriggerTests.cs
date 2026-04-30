#nullable enable
using System;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;

/// <summary>
/// Unit tests for <see cref="WhiteGloveInventoryTrigger"/>. Verifies single-fire
/// dedup, action-on-event, and clean unsubscribe-on-dispose. The trigger is the V2
/// agent companion of backend's vulnerability-correlate queue (PR1+PR2): it fires the
/// Part-1 software-inventory snapshot while the OOBE Reseal dialog is still up.
/// </summary>
public sealed class WhiteGloveInventoryTriggerTests : IDisposable
{
    private readonly TempDirectory _tmp = new();
    private readonly AgentLogger _logger;

    public WhiteGloveInventoryTriggerTests()
    {
        _logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
    }

    public void Dispose()
    {
        try { _tmp.Dispose(); } catch { }
    }

    [Fact]
    public void OnWhiteGloveCompleted_invokes_trigger_action_once()
    {
        var source = new FakeWhiteGloveSource();
        var triggerCount = 0;
        using var sut = new WhiteGloveInventoryTrigger(
            host: source,
            onTrigger: () => triggerCount++,
            logger: _logger);

        source.RaiseWhiteGloveCompleted();

        Assert.Equal(1, triggerCount);
    }

    [Fact]
    public void OnWhiteGloveCompleted_dedups_duplicate_event_emissions()
    {
        var source = new FakeWhiteGloveSource();
        var triggerCount = 0;
        using var sut = new WhiteGloveInventoryTrigger(
            host: source,
            onTrigger: () => triggerCount++,
            logger: _logger);

        source.RaiseWhiteGloveCompleted();
        source.RaiseWhiteGloveCompleted();
        source.RaiseWhiteGloveCompleted();

        Assert.Equal(1, triggerCount);
    }

    [Fact]
    public void Dispose_unsubscribes_so_late_events_are_ignored()
    {
        var source = new FakeWhiteGloveSource();
        var triggerCount = 0;
        var sut = new WhiteGloveInventoryTrigger(
            host: source,
            onTrigger: () => triggerCount++,
            logger: _logger);

        sut.Dispose();
        source.RaiseWhiteGloveCompleted();

        Assert.Equal(0, triggerCount);
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var source = new FakeWhiteGloveSource();
        using var sut = new WhiteGloveInventoryTrigger(
            host: source,
            onTrigger: () => { },
            logger: _logger);

        sut.Dispose();
        sut.Dispose(); // must not throw or double-unsubscribe
    }

    [Fact]
    public void Trigger_action_exception_does_not_propagate()
    {
        var source = new FakeWhiteGloveSource();
        using var sut = new WhiteGloveInventoryTrigger(
            host: source,
            onTrigger: () => throw new InvalidOperationException("boom"),
            logger: _logger);

        // Must not surface to the source's event-loop — the agent must keep running
        // even if the analyzer manager is mid-shutdown when the trigger fires.
        var ex = Record.Exception(() => source.RaiseWhiteGloveCompleted());
        Assert.Null(ex);
    }

    [Fact]
    public void Constructor_throws_on_null_host()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new WhiteGloveInventoryTrigger(host: null!, onTrigger: () => { }, logger: _logger));
    }

    [Fact]
    public void Constructor_throws_on_null_action()
    {
        var source = new FakeWhiteGloveSource();
        Assert.Throws<ArgumentNullException>(() =>
            new WhiteGloveInventoryTrigger(host: source, onTrigger: null!, logger: _logger));
    }

    [Fact]
    public void Constructor_throws_on_null_logger()
    {
        var source = new FakeWhiteGloveSource();
        Assert.Throws<ArgumentNullException>(() =>
            new WhiteGloveInventoryTrigger(host: source, onTrigger: () => { }, logger: null!));
    }

    private sealed class FakeWhiteGloveSource : IWhiteGloveCompletedSource
    {
        public event EventHandler? WhiteGloveCompleted;

        public void RaiseWhiteGloveCompleted() => WhiteGloveCompleted?.Invoke(this, EventArgs.Empty);
    }
}
