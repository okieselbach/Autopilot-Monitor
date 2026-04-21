using AutopilotMonitor.Functions.Services.Indexing;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Routing tests for <see cref="IndexReconcileHandler"/> (Plan §2.8, §M5.d.3). Verifies
/// the 0–3 fan-out decisions per envelope shape using a capture-only fake repo —
/// the real <c>TableIndexRepository</c> mapping is covered by <see cref="TableIndexRepositoryMappingTests"/>.
/// </summary>
public class IndexReconcileHandlerTests
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

    // ============================================================ Signal routing

    [Fact]
    public async Task Signal_envelope_writes_exactly_one_SignalsByKind_row()
    {
        var (handler, repo) = Sut();

        await handler.HandleAsync(new IndexReconcileEnvelope
        {
            SourceKind           = "Signal",
            TenantId             = TenantId,
            SessionId            = SessionId,
            OccurredAtUtc        = Occurred,
            SessionSignalOrdinal = 42,
            SignalKind           = "EspTerminalFailure",
            SourceOrigin         = "EspAndHelloTrackerAdapter",
        });

        Assert.Single(repo.SignalsByKind);
        var row = repo.SignalsByKind[0];
        Assert.Equal(TenantId, row.TenantId);
        Assert.Equal(SessionId, row.SessionId);
        Assert.Equal("EspTerminalFailure", row.SignalKind);
        Assert.Equal(42L, row.SessionSignalOrdinal);
        Assert.Equal("EspAndHelloTrackerAdapter", row.SourceOrigin);
        Assert.Equal(Occurred, row.OccurredAtUtc);

        // Other tables untouched.
        Assert.Empty(repo.SessionsByTerminal);
        Assert.Empty(repo.SessionsByStage);
        Assert.Empty(repo.DeadEndsByReason);
        Assert.Empty(repo.ClassifierVerdictsByIdLevel);
    }

    [Fact]
    public async Task Signal_envelope_missing_ordinal_is_dropped()
    {
        var (handler, repo) = Sut();

        await handler.HandleAsync(new IndexReconcileEnvelope
        {
            SourceKind           = "Signal",
            TenantId             = TenantId,
            SessionId            = SessionId,
            OccurredAtUtc        = Occurred,
            SessionSignalOrdinal = null,
            SignalKind           = "EspPhaseChanged",
        });

        Assert.Empty(repo.SignalsByKind);
    }

    [Fact]
    public async Task Signal_envelope_missing_kind_is_dropped()
    {
        var (handler, repo) = Sut();

        await handler.HandleAsync(new IndexReconcileEnvelope
        {
            SourceKind           = "Signal",
            TenantId             = TenantId,
            SessionId            = SessionId,
            OccurredAtUtc        = Occurred,
            SessionSignalOrdinal = 1,
            SignalKind           = string.Empty,
        });

        Assert.Empty(repo.SignalsByKind);
    }

    // ============================================================ DecisionTransition routing — all 4 paths

    [Fact]
    public async Task DecisionTransition_terminal_taken_with_classifier_writes_three_index_rows()
    {
        var (handler, repo) = Sut();

        // WhiteGlove-sealing: Taken=true, IsTerminal=true, classifier verdict attached.
        // Expected: SessionsByTerminal + SessionsByStage + ClassifierVerdictsByIdLevel (3 rows).
        await handler.HandleAsync(new IndexReconcileEnvelope
        {
            SourceKind                = "DecisionTransition",
            TenantId                  = TenantId,
            SessionId                 = SessionId,
            OccurredAtUtc             = Occurred,
            StepIndex                 = 7,
            FromStage                 = "EspInProgress",
            ToStage                   = "WhiteGloveSealed",
            Taken                     = true,
            IsTerminal                = true,
            ClassifierVerdictId       = "whiteglove-sealing",
            ClassifierHypothesisLevel = "Strong",
        });

        Assert.Single(repo.SessionsByTerminal);
        Assert.Equal("WhiteGloveSealed", repo.SessionsByTerminal[0].TerminalStage);
        Assert.Equal(7, repo.SessionsByTerminal[0].StepIndex);

        Assert.Single(repo.SessionsByStage);
        Assert.Equal("WhiteGloveSealed", repo.SessionsByStage[0].Stage);
        Assert.Equal(Occurred, repo.SessionsByStage[0].LastUpdatedUtc);

        Assert.Single(repo.ClassifierVerdictsByIdLevel);
        Assert.Equal("whiteglove-sealing", repo.ClassifierVerdictsByIdLevel[0].ClassifierId);
        Assert.Equal("Strong", repo.ClassifierVerdictsByIdLevel[0].HypothesisLevel);

        // Dead-end path not triggered for Taken=true.
        Assert.Empty(repo.DeadEndsByReason);
        // Signal path never touched on a DecisionTransition envelope.
        Assert.Empty(repo.SignalsByKind);
    }

    [Fact]
    public async Task DecisionTransition_taken_non_terminal_no_classifier_writes_only_SessionsByStage()
    {
        var (handler, repo) = Sut();

        await handler.HandleAsync(new IndexReconcileEnvelope
        {
            SourceKind    = "DecisionTransition",
            TenantId      = TenantId,
            SessionId     = SessionId,
            OccurredAtUtc = Occurred,
            StepIndex     = 3,
            FromStage     = "AwaitingEsp",
            ToStage       = "EspInProgress",
            Taken         = true,
            IsTerminal    = false,
        });

        Assert.Empty(repo.SessionsByTerminal);
        Assert.Single(repo.SessionsByStage);
        Assert.Empty(repo.DeadEndsByReason);
        Assert.Empty(repo.ClassifierVerdictsByIdLevel);
    }

    [Fact]
    public async Task DecisionTransition_dead_end_with_classifier_writes_DeadEndsByReason_and_verdict()
    {
        var (handler, repo) = Sut();

        await handler.HandleAsync(new IndexReconcileEnvelope
        {
            SourceKind                = "DecisionTransition",
            TenantId                  = TenantId,
            SessionId                 = SessionId,
            OccurredAtUtc             = Occurred,
            StepIndex                 = 5,
            FromStage                 = "EspInProgress",
            ToStage                   = "EspInProgress",
            Taken                     = false,
            IsTerminal                = false,
            DeadEndReason             = "hybrid_reboot_gate_blocking",
            ClassifierVerdictId       = "whiteglove-sealing",
            ClassifierHypothesisLevel = "Weak",
        });

        Assert.Single(repo.DeadEndsByReason);
        Assert.Equal("hybrid_reboot_gate_blocking", repo.DeadEndsByReason[0].DeadEndReason);
        Assert.Equal("EspInProgress", repo.DeadEndsByReason[0].FromStage);
        Assert.Equal("EspInProgress", repo.DeadEndsByReason[0].AttemptedToStage);

        Assert.Single(repo.ClassifierVerdictsByIdLevel);
        Assert.Equal("Weak", repo.ClassifierVerdictsByIdLevel[0].HypothesisLevel);

        // Taken=false → SessionsByStage is NOT updated (no stage entry).
        Assert.Empty(repo.SessionsByStage);
        Assert.Empty(repo.SessionsByTerminal);
    }

    [Fact]
    public async Task DecisionTransition_dead_end_without_reason_writes_nothing_to_DeadEnds()
    {
        // Defense in depth: an envelope with Taken=false but no reason should not create a
        // DeadEnd row (empty reason would collide into a single PK across unrelated blocks).
        var (handler, repo) = Sut();

        await handler.HandleAsync(new IndexReconcileEnvelope
        {
            SourceKind    = "DecisionTransition",
            TenantId      = TenantId,
            SessionId     = SessionId,
            OccurredAtUtc = Occurred,
            StepIndex     = 1,
            FromStage     = "A",
            ToStage       = "A",
            Taken         = false,
            DeadEndReason = null,
        });

        Assert.Empty(repo.DeadEndsByReason);
    }

    [Fact]
    public async Task DecisionTransition_missing_stepIndex_is_dropped_entirely()
    {
        var (handler, repo) = Sut();

        await handler.HandleAsync(new IndexReconcileEnvelope
        {
            SourceKind    = "DecisionTransition",
            TenantId      = TenantId,
            SessionId     = SessionId,
            OccurredAtUtc = Occurred,
            StepIndex     = null,
            ToStage       = "WhiteGloveSealed",
            Taken         = true,
            IsTerminal    = true,
        });

        Assert.Empty(repo.SessionsByTerminal);
        Assert.Empty(repo.SessionsByStage);
        Assert.Empty(repo.DeadEndsByReason);
        Assert.Empty(repo.ClassifierVerdictsByIdLevel);
    }

    // ============================================================ Misc

    [Fact]
    public async Task Unknown_SourceKind_is_logged_and_dropped()
    {
        var (handler, repo) = Sut();

        await handler.HandleAsync(new IndexReconcileEnvelope
        {
            SourceKind    = "FutureThingWeDontKnowAbout",
            TenantId      = TenantId,
            SessionId     = SessionId,
            OccurredAtUtc = Occurred,
        });

        Assert.Empty(repo.SignalsByKind);
        Assert.Empty(repo.SessionsByTerminal);
        Assert.Empty(repo.SessionsByStage);
        Assert.Empty(repo.DeadEndsByReason);
        Assert.Empty(repo.ClassifierVerdictsByIdLevel);
    }

    [Fact]
    public async Task Null_envelope_is_logged_and_dropped()
    {
        var (handler, repo) = Sut();

        await handler.HandleAsync(null!);

        Assert.Empty(repo.SignalsByKind);
    }

    // ============================================================ FakeIndexTableRepository

    private sealed class FakeIndexTableRepository : IIndexTableRepository
    {
        public List<SessionsByTerminalRecord>          SessionsByTerminal          { get; } = new();
        public List<SessionsByStageRecord>             SessionsByStage             { get; } = new();
        public List<DeadEndsByReasonRecord>            DeadEndsByReason            { get; } = new();
        public List<ClassifierVerdictsByIdLevelRecord> ClassifierVerdictsByIdLevel { get; } = new();
        public List<SignalsByKindRecord>               SignalsByKind               { get; } = new();

        public Task<int> StoreSessionsByTerminalAsync(
            IReadOnlyList<SessionsByTerminalRecord> records, CancellationToken cancellationToken = default)
        {
            SessionsByTerminal.AddRange(records);
            return Task.FromResult(records.Count);
        }

        public Task<int> StoreSessionsByStageAsync(
            IReadOnlyList<SessionsByStageRecord> records, CancellationToken cancellationToken = default)
        {
            SessionsByStage.AddRange(records);
            return Task.FromResult(records.Count);
        }

        public Task<int> StoreDeadEndsByReasonAsync(
            IReadOnlyList<DeadEndsByReasonRecord> records, CancellationToken cancellationToken = default)
        {
            DeadEndsByReason.AddRange(records);
            return Task.FromResult(records.Count);
        }

        public Task<int> StoreClassifierVerdictsByIdLevelAsync(
            IReadOnlyList<ClassifierVerdictsByIdLevelRecord> records, CancellationToken cancellationToken = default)
        {
            ClassifierVerdictsByIdLevel.AddRange(records);
            return Task.FromResult(records.Count);
        }

        public Task<int> StoreSignalsByKindAsync(
            IReadOnlyList<SignalsByKindRecord> records, CancellationToken cancellationToken = default)
        {
            SignalsByKind.AddRange(records);
            return Task.FromResult(records.Count);
        }
    }
}
