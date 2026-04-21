using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Services.Indexing;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <b>M5 release-gate</b> (Plan §M5-Pflicht line 1395): per-index-table PK/RK consistency
/// between primary rows and their derived index rows. For each index table, walk a
/// canonical primary row through the Factory + Handler pipeline and verify that the
/// captured index record carries enough back-ref information to reconstruct the
/// primary's <c>(PartitionKey, RowKey)</c> via <see cref="TableSignalRepository"/> /
/// <see cref="TableDecisionTransitionRepository"/> key builders.
/// <para>
/// Also asserts pipeline idempotency: running the same primary row through twice
/// produces identical upsert keys — the consumer's <c>UpsertReplace</c> batches stay
/// deduplicating even under timer-triggered resubmits.
/// </para>
/// </summary>
public class IndexDualWriteConsistencyTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    private static readonly DateTime Occurred =
        new(2026, 4, 21, 14, 7, 33, DateTimeKind.Utc);

    private static (IndexReconcileHandler handler, FakeIndexTableRepository repo) Sut()
    {
        var repo = new FakeIndexTableRepository();
        var handler = new IndexReconcileHandler(repo, NullLogger<IndexReconcileHandler>.Instance);
        return (handler, repo);
    }

    // ============================================================ SignalsByKind ↔ Signals

    [Fact]
    public async Task SignalsByKind_row_carries_back_ref_to_primary_Signals_row()
    {
        var primary = new SignalRecord
        {
            TenantId             = TenantId,
            SessionId            = SessionId,
            SessionSignalOrdinal = 42,
            Kind                 = "EspTerminalFailure",
            OccurredAtUtc        = Occurred,
            SourceOrigin         = "EspAndHelloTrackerAdapter",
        };

        var (handler, repo) = Sut();
        await handler.HandleAsync(IndexReconcileEnvelopeFactory.FromSignal(primary));

        var index = Assert.Single(repo.SignalsByKind);

        // Back-ref fields on the index row must reconstruct the primary PK/RK.
        var reconstructedPk = TableSignalRepository.BuildPartitionKey(index.TenantId, index.SessionId);
        var reconstructedRk = TableSignalRepository.BuildRowKey(index.SessionSignalOrdinal);

        Assert.Equal(
            TableSignalRepository.BuildPartitionKey(primary.TenantId, primary.SessionId),
            reconstructedPk);
        Assert.Equal(
            TableSignalRepository.BuildRowKey(primary.SessionSignalOrdinal),
            reconstructedRk);
    }

    // ============================================================ SessionsByTerminal ↔ DecisionTransitions

    [Fact]
    public async Task SessionsByTerminal_row_carries_back_ref_to_primary_DecisionTransitions_row()
    {
        var primary = TerminalTransition();

        var (handler, repo) = Sut();
        await handler.HandleAsync(IndexReconcileEnvelopeFactory.FromDecisionTransition(primary));

        var index = Assert.Single(repo.SessionsByTerminal);

        var reconstructedPk = TableDecisionTransitionRepository.BuildPartitionKey(index.TenantId, index.SessionId);
        var reconstructedRk = TableDecisionTransitionRepository.BuildRowKey(index.StepIndex);

        Assert.Equal(
            TableDecisionTransitionRepository.BuildPartitionKey(primary.TenantId, primary.SessionId),
            reconstructedPk);
        Assert.Equal(
            TableDecisionTransitionRepository.BuildRowKey(primary.StepIndex),
            reconstructedRk);
    }

    [Fact]
    public async Task SessionsByStage_row_carries_back_ref_to_primary_DecisionTransitions_row()
    {
        var primary = new DecisionTransitionRecord
        {
            TenantId      = TenantId,
            SessionId     = SessionId,
            StepIndex     = 3,
            FromStage     = "AwaitingEsp",
            ToStage       = "EspInProgress",
            Taken         = true,
            IsTerminal    = false,
            OccurredAtUtc = Occurred,
        };

        var (handler, repo) = Sut();
        await handler.HandleAsync(IndexReconcileEnvelopeFactory.FromDecisionTransition(primary));

        var index = Assert.Single(repo.SessionsByStage);

        var reconstructedPk = TableDecisionTransitionRepository.BuildPartitionKey(index.TenantId, index.SessionId);
        var reconstructedRk = TableDecisionTransitionRepository.BuildRowKey(index.StepIndex);

        Assert.Equal(
            TableDecisionTransitionRepository.BuildPartitionKey(primary.TenantId, primary.SessionId),
            reconstructedPk);
        Assert.Equal(
            TableDecisionTransitionRepository.BuildRowKey(primary.StepIndex),
            reconstructedRk);
    }

    [Fact]
    public async Task DeadEndsByReason_row_carries_back_ref_to_primary_DecisionTransitions_row()
    {
        var primary = new DecisionTransitionRecord
        {
            TenantId      = TenantId,
            SessionId     = SessionId,
            StepIndex     = 5,
            FromStage     = "EspInProgress",
            ToStage       = "EspInProgress",
            Taken         = false,
            IsTerminal    = false,
            DeadEndReason = "hybrid_reboot_gate_blocking",
            OccurredAtUtc = Occurred,
        };

        var (handler, repo) = Sut();
        await handler.HandleAsync(IndexReconcileEnvelopeFactory.FromDecisionTransition(primary));

        var index = Assert.Single(repo.DeadEndsByReason);

        Assert.Equal(
            TableDecisionTransitionRepository.BuildPartitionKey(primary.TenantId, primary.SessionId),
            TableDecisionTransitionRepository.BuildPartitionKey(index.TenantId, index.SessionId));
        Assert.Equal(
            TableDecisionTransitionRepository.BuildRowKey(primary.StepIndex),
            TableDecisionTransitionRepository.BuildRowKey(index.StepIndex));
    }

    [Fact]
    public async Task ClassifierVerdictsByIdLevel_row_carries_back_ref_to_primary_DecisionTransitions_row()
    {
        var primary = new DecisionTransitionRecord
        {
            TenantId                  = TenantId,
            SessionId                 = SessionId,
            StepIndex                 = 7,
            FromStage                 = "EspInProgress",
            ToStage                   = "WhiteGloveSealed",
            Taken                     = true,
            IsTerminal                = true,
            ClassifierVerdictId       = "whiteglove-sealing",
            ClassifierHypothesisLevel = "Strong",
            OccurredAtUtc             = Occurred,
        };

        var (handler, repo) = Sut();
        await handler.HandleAsync(IndexReconcileEnvelopeFactory.FromDecisionTransition(primary));

        var index = Assert.Single(repo.ClassifierVerdictsByIdLevel);

        Assert.Equal(
            TableDecisionTransitionRepository.BuildPartitionKey(primary.TenantId, primary.SessionId),
            TableDecisionTransitionRepository.BuildPartitionKey(index.TenantId, index.SessionId));
        Assert.Equal(
            TableDecisionTransitionRepository.BuildRowKey(primary.StepIndex),
            TableDecisionTransitionRepository.BuildRowKey(index.StepIndex));
    }

    // ============================================================ pipeline idempotency

    [Fact]
    public async Task Resubmitting_same_primary_rows_produces_same_index_keys()
    {
        // Simulates the M5.d.4 timer re-enqueueing primary rows it already handled —
        // the handler's index-row keys must be key-stable so the real TableIndexRepository's
        // UpsertReplace collapses duplicates rather than appending rows.
        var (handler, repo) = Sut();

        var signal = new SignalRecord
        {
            TenantId             = TenantId,
            SessionId            = SessionId,
            SessionSignalOrdinal = 1,
            Kind                 = "DesktopArrived",
            OccurredAtUtc        = Occurred,
        };
        var transition = TerminalTransition();

        // First pass: initial ingest fan-out.
        await handler.HandleAsync(IndexReconcileEnvelopeFactory.FromSignal(signal));
        await handler.HandleAsync(IndexReconcileEnvelopeFactory.FromDecisionTransition(transition));

        // Second pass: 2h-timer reconcile resubmits the same primaries.
        await handler.HandleAsync(IndexReconcileEnvelopeFactory.FromSignal(signal));
        await handler.HandleAsync(IndexReconcileEnvelopeFactory.FromDecisionTransition(transition));

        // The fake repo captures one row per call, but all duplicated rows must map to the
        // same (PK, RK) — the real Azure Tables Upsert would collapse them.
        Assert.Equal(2, repo.SignalsByKind.Count);
        var signalKeys = repo.SignalsByKind
            .Select(r => (
                Pk: IndexRowKeys.BuildSignalsByKindPk(r.TenantId, r.SignalKind),
                Rk: IndexRowKeys.BuildSignalsByKindRk(r.OccurredAtUtc, r.SessionId, r.SessionSignalOrdinal)))
            .Distinct()
            .ToList();
        Assert.Single(signalKeys);

        Assert.Equal(2, repo.SessionsByTerminal.Count);
        var terminalKeys = repo.SessionsByTerminal
            .Select(r => (
                Pk: IndexRowKeys.BuildSessionsByTerminalPk(r.TenantId, r.TerminalStage),
                Rk: IndexRowKeys.BuildSessionsByTerminalRk(r.OccurredAtUtc, r.SessionId)))
            .Distinct()
            .ToList();
        Assert.Single(terminalKeys);

        Assert.Equal(2, repo.SessionsByStage.Count);
        var stageKeys = repo.SessionsByStage
            .Select(r => (
                Pk: IndexRowKeys.BuildSessionsByStagePk(r.TenantId, r.Stage),
                Rk: IndexRowKeys.BuildSessionsByStageRk(r.LastUpdatedUtc, r.SessionId)))
            .Distinct()
            .ToList();
        Assert.Single(stageKeys);

        Assert.Equal(2, repo.ClassifierVerdictsByIdLevel.Count);
        var verdictKeys = repo.ClassifierVerdictsByIdLevel
            .Select(r => (
                Pk: IndexRowKeys.BuildClassifierVerdictsByIdLevelPk(r.TenantId, r.ClassifierId, r.HypothesisLevel),
                Rk: IndexRowKeys.BuildClassifierVerdictsByIdLevelRk(r.OccurredAtUtc, r.SessionId, r.StepIndex)))
            .Distinct()
            .ToList();
        Assert.Single(verdictKeys);
    }

    // ============================================================ helpers

    private static DecisionTransitionRecord TerminalTransition()
        => new()
        {
            TenantId                  = TenantId,
            SessionId                 = SessionId,
            StepIndex                 = 7,
            FromStage                 = "EspInProgress",
            ToStage                   = "WhiteGloveSealed",
            Taken                     = true,
            IsTerminal                = true,
            ClassifierVerdictId       = "whiteglove-sealing",
            ClassifierHypothesisLevel = "Strong",
            OccurredAtUtc             = Occurred,
        };

    private sealed class FakeIndexTableRepository : IIndexTableRepository
    {
        public List<SessionsByTerminalRecord>          SessionsByTerminal          { get; } = new();
        public List<SessionsByStageRecord>             SessionsByStage             { get; } = new();
        public List<DeadEndsByReasonRecord>            DeadEndsByReason            { get; } = new();
        public List<ClassifierVerdictsByIdLevelRecord> ClassifierVerdictsByIdLevel { get; } = new();
        public List<SignalsByKindRecord>               SignalsByKind               { get; } = new();

        public Task<int> StoreSessionsByTerminalAsync(IReadOnlyList<SessionsByTerminalRecord> records, CancellationToken ct = default)
        { SessionsByTerminal.AddRange(records); return Task.FromResult(records.Count); }
        public Task<int> StoreSessionsByStageAsync(IReadOnlyList<SessionsByStageRecord> records, CancellationToken ct = default)
        { SessionsByStage.AddRange(records); return Task.FromResult(records.Count); }
        public Task<int> StoreDeadEndsByReasonAsync(IReadOnlyList<DeadEndsByReasonRecord> records, CancellationToken ct = default)
        { DeadEndsByReason.AddRange(records); return Task.FromResult(records.Count); }
        public Task<int> StoreClassifierVerdictsByIdLevelAsync(IReadOnlyList<ClassifierVerdictsByIdLevelRecord> records, CancellationToken ct = default)
        { ClassifierVerdictsByIdLevel.AddRange(records); return Task.FromResult(records.Count); }
        public Task<int> StoreSignalsByKindAsync(IReadOnlyList<SignalsByKindRecord> records, CancellationToken ct = default)
        { SignalsByKind.AddRange(records); return Task.FromResult(records.Count); }
    }
}
