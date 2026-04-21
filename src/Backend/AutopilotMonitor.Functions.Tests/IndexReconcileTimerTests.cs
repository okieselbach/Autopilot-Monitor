using AutopilotMonitor.Functions.Functions.Admin;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for the 2h safety-net <see cref="IndexReconcileTimer"/> (Plan §2.8, §M5.d.4).
/// Covers the flag-gate short-circuit, the happy path (enqueue every primary row
/// in the window), and the empty-window case.
/// </summary>
public class IndexReconcileTimerTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    private static readonly DateTime Cutoff = new(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc);

    // ============================================================ flag-off short-circuit

    [Fact]
    public async Task Feature_flag_off_skips_queries_and_enqueue()
    {
        var adminConfigSvc = BuildAdminConfigService(enableIndexDualWrite: false);
        var signalRepo     = new Mock<ISignalRepository>(MockBehavior.Strict);
        var transitionRepo = new Mock<IDecisionTransitionRepository>(MockBehavior.Strict);
        var producer       = new Mock<IIndexReconcileProducer>(MockBehavior.Strict);

        var timer = new IndexReconcileTimer(
            adminConfigSvc,
            signalRepo.Object,
            transitionRepo.Object,
            producer.Object,
            NullLogger<IndexReconcileTimer>.Instance);

        await timer.RunReconcileAsync(Cutoff, default);

        // Strict mocks throw if any unconfigured call happens — so zero setups + no throw
        // proves the timer short-circuited before any query / enqueue.
        signalRepo.VerifyNoOtherCalls();
        transitionRepo.VerifyNoOtherCalls();
        producer.VerifyNoOtherCalls();
    }

    // ============================================================ happy path

    [Fact]
    public async Task Flag_on_with_primary_rows_enqueues_all_envelopes()
    {
        var adminConfigSvc = BuildAdminConfigService(enableIndexDualWrite: true);

        var signals = new List<SignalRecord>
        {
            new() { TenantId = TenantId, SessionId = SessionId, SessionSignalOrdinal = 1, Kind = "EspPhaseChanged", OccurredAtUtc = Cutoff.AddMinutes(5) },
            new() { TenantId = TenantId, SessionId = SessionId, SessionSignalOrdinal = 2, Kind = "DesktopArrived",   OccurredAtUtc = Cutoff.AddMinutes(10) },
        };
        var transitions = new List<DecisionTransitionRecord>
        {
            new() { TenantId = TenantId, SessionId = SessionId, StepIndex = 1, Taken = true, FromStage = "A", ToStage = "B", OccurredAtUtc = Cutoff.AddMinutes(7) },
        };

        var signalRepo = new Mock<ISignalRepository>();
        signalRepo
            .Setup(r => r.QueryByTimestampAtOrAfterAsync(Cutoff, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(signals);
        var transitionRepo = new Mock<IDecisionTransitionRepository>();
        transitionRepo
            .Setup(r => r.QueryByTimestampAtOrAfterAsync(Cutoff, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(transitions);

        List<IndexReconcileEnvelope>? captured = null;
        var producer = new Mock<IIndexReconcileProducer>();
        producer
            .Setup(p => p.EnqueueBatchAsync(It.IsAny<IReadOnlyList<IndexReconcileEnvelope>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<IndexReconcileEnvelope>, CancellationToken>((e, _) => captured = e.ToList())
            .ReturnsAsync(3);

        var timer = new IndexReconcileTimer(
            adminConfigSvc,
            signalRepo.Object,
            transitionRepo.Object,
            producer.Object,
            NullLogger<IndexReconcileTimer>.Instance);

        await timer.RunReconcileAsync(Cutoff, default);

        Assert.NotNull(captured);
        Assert.Equal(3, captured!.Count);
        Assert.Equal("Signal",             captured[0].SourceKind);
        Assert.Equal(1L,                   captured[0].SessionSignalOrdinal);
        Assert.Equal("Signal",             captured[1].SourceKind);
        Assert.Equal(2L,                   captured[1].SessionSignalOrdinal);
        Assert.Equal("DecisionTransition", captured[2].SourceKind);
        Assert.Equal(1,                    captured[2].StepIndex);
    }

    // ============================================================ empty-window: no enqueue

    [Fact]
    public async Task Flag_on_empty_window_skips_enqueue()
    {
        var adminConfigSvc = BuildAdminConfigService(enableIndexDualWrite: true);

        var signalRepo = new Mock<ISignalRepository>();
        signalRepo
            .Setup(r => r.QueryByTimestampAtOrAfterAsync(Cutoff, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SignalRecord>());
        var transitionRepo = new Mock<IDecisionTransitionRepository>();
        transitionRepo
            .Setup(r => r.QueryByTimestampAtOrAfterAsync(Cutoff, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DecisionTransitionRecord>());

        var producer = new Mock<IIndexReconcileProducer>(MockBehavior.Strict);

        var timer = new IndexReconcileTimer(
            adminConfigSvc,
            signalRepo.Object,
            transitionRepo.Object,
            producer.Object,
            NullLogger<IndexReconcileTimer>.Instance);

        await timer.RunReconcileAsync(Cutoff, default);

        // Empty batch → no producer call (strict mock throws otherwise).
        producer.VerifyNoOtherCalls();
    }

    // ============================================================ cutoff window invariant

    [Fact]
    public void ReconcileWindow_is_4_hours_and_larger_than_timer_cadence()
    {
        // The 2h cron fires every 2h but scans a 4h window — so every primary row is
        // re-checked at least twice before falling out of range. Protect the invariant.
        Assert.Equal(TimeSpan.FromHours(4), IndexReconcileTimer.ReconcileWindow);
    }

    // ============================================================ helpers

    private static AdminConfigurationService BuildAdminConfigService(bool enableIndexDualWrite)
    {
        var mock = new Mock<AdminConfigurationService>(
            Mock.Of<IConfigRepository>(),
            NullLogger<AdminConfigurationService>.Instance,
            new MemoryCache(new MemoryCacheOptions()));

        mock.Setup(s => s.GetConfigurationAsync())
            .ReturnsAsync(new AdminConfiguration { EnableIndexDualWrite = enableIndexDualWrite });

        return mock.Object;
    }
}
