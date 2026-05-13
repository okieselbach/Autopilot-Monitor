using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Per-tenant retention dispatch + rate-limit tests for <see cref="SessionRetentionFanoutService"/>
/// (Plan §5 PR6 step 7).
/// </summary>
public class SessionRetentionFanoutServiceTests
{
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public async Task RunAsync_dispatches_V2_path_when_EnableCascadeDeleteV2_is_true()
    {
        var harness = new Harness();
        harness.WithTenant(TenantA, retentionDays: 30, v2: true,
            sessions: new[] { Old("s1", 60), Old("s2", 45) });

        var result = await harness.Sut.RunAsync(CancellationToken.None);

        Assert.Equal(2, result.SessionsEnqueued);
        Assert.Equal(0, result.SessionsLegacyDeleted);
        harness.Enqueuer.Verify(
            e => e.EnqueueAsync(TenantA, "s1", "retention_cutoff", It.Is<DeletionActor>(a => a.Type == "maintenance"), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        harness.Enqueuer.Verify(
            e => e.EnqueueAsync(TenantA, "s2", "retention_cutoff", It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_falls_back_to_legacy_direct_delete_when_EnableCascadeDeleteV2_is_false()
    {
        var harness = new Harness();
        harness.WithTenant(TenantA, retentionDays: 30, v2: false,
            sessions: new[] { Old("s1", 60) });
        harness.MaintenanceRepo
            .Setup(m => m.DeleteSessionEventsAsync(TenantA, "s1"))
            .ReturnsAsync(17);
        harness.SessionRepo
            .Setup(r => r.DeleteSessionAsync(TenantA, "s1"))
            .ReturnsAsync(true);

        var result = await harness.Sut.RunAsync(CancellationToken.None);

        Assert.Equal(0, result.SessionsEnqueued);
        Assert.Equal(1, result.SessionsLegacyDeleted);
        harness.Enqueuer.Verify(
            e => e.EnqueueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.SessionRepo.Verify(r => r.DeleteSessionAsync(TenantA, "s1"), Times.Once);
    }

    [Fact]
    public async Task RunAsync_legacy_path_counts_DeleteSessionAsync_false_as_skip_not_delete()
    {
        // PR5 finding 2: DeleteSessionAsync returns false when a V2 cascade has claimed the row.
        // The fanout must record that as a skip so the next maintenance tick retries.
        var harness = new Harness();
        harness.WithTenant(TenantA, retentionDays: 30, v2: false, sessions: new[] { Old("s1", 60) });
        harness.SessionRepo.Setup(r => r.DeleteSessionAsync(TenantA, "s1")).ReturnsAsync(false);

        var result = await harness.Sut.RunAsync(CancellationToken.None);

        Assert.Equal(0, result.SessionsLegacyDeleted);
        Assert.Equal(1, result.SessionsSkipped);
    }

    [Fact]
    public async Task RunAsync_handles_two_tenants_with_different_retentions()
    {
        var harness = new Harness();
        // Tenant A: 30d retention → eligible at 45d
        harness.WithTenant(TenantA, retentionDays: 30, v2: true, sessions: new[] { Old("a1", 45) });
        // Tenant B: 120d retention → 45d-old session is NOT eligible (only 200d-old is)
        harness.WithTenant(TenantB, retentionDays: 120, v2: true, sessions: new[] { Old("b1", 45), Old("b2", 200) });

        // The repo only returns sessions older than each cutoff. Wire each tenant's mock to honor that.
        harness.MaintenanceRepo.Setup(m => m.GetSessionsOlderThanAsync(TenantA, It.IsAny<DateTime>()))
            .ReturnsAsync(new List<SessionSummary> { Summary(TenantA, "a1") });
        harness.MaintenanceRepo.Setup(m => m.GetSessionsOlderThanAsync(TenantB, It.Is<DateTime>(d => d <= DateTime.UtcNow.AddDays(-120))))
            .ReturnsAsync(new List<SessionSummary> { Summary(TenantB, "b2") });

        var result = await harness.Sut.RunAsync(CancellationToken.None);

        Assert.Equal(2, result.TenantsProcessed);
        Assert.Equal(2, result.SessionsEnqueued);
        harness.Enqueuer.Verify(e => e.EnqueueAsync(TenantA, "a1", "retention_cutoff", It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.Enqueuer.Verify(e => e.EnqueueAsync(TenantB, "b2", "retention_cutoff", It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()), Times.Once);
        // b1 must NOT be enqueued — its session age is below the 120d cutoff for Tenant B.
        harness.Enqueuer.Verify(e => e.EnqueueAsync(TenantB, "b1", It.IsAny<string>(), It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_enforces_rate_limit_per_tenant_per_run()
    {
        var harness = new Harness();
        var many = new List<SessionSummary>();
        for (int i = 0; i < SessionRetentionFanoutService.MaxEnqueuesPerTenantPerRun + 25; i++)
            many.Add(Summary(TenantA, $"s{i:000}"));
        harness.WithTenantOverride(TenantA, retentionDays: 30, v2: true, sessions: many);

        var result = await harness.Sut.RunAsync(CancellationToken.None);

        Assert.Equal(SessionRetentionFanoutService.MaxEnqueuesPerTenantPerRun, result.SessionsEnqueued);
        Assert.Equal(1, result.RateLimitedTenants);
        // The 101st session onward must NOT have been touched.
        harness.Enqueuer.Verify(
            e => e.EnqueueAsync(TenantA, "s100", It.IsAny<string>(), It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_skips_tenant_with_DataRetentionDays_zero()
    {
        var harness = new Harness();
        harness.WithTenant(TenantA, retentionDays: 0, v2: true, sessions: new[] { Old("s1", 365) });

        var result = await harness.Sut.RunAsync(CancellationToken.None);

        Assert.Equal(0, result.SessionsEnqueued);
        Assert.Equal(0, result.SessionsLegacyDeleted);
        // GetSessionsOlderThanAsync must not have been called for that tenant.
        harness.MaintenanceRepo.Verify(m => m.GetSessionsOlderThanAsync(TenantA, It.IsAny<DateTime>()), Times.Never);
    }

    // ────────────────────────────────────────────────────────────────────────── PR6 follow-up F1 ─

    [Fact]
    public async Task RunAsync_legacy_path_skips_locked_session_WITHOUT_touching_side_tables()
    {
        // PR6 follow-up F1: pre-read DeletionState before any side-table mutation. Without this
        // gate the legacy path would delete Events / RuleResults / AppSummaries and only discover
        // the V2 cascade lock at the tombstone CAS — leaving the V2 cascade's manifest claiming
        // rows that were silently removed in the meantime.
        var harness = new Harness();
        harness.WithTenant(TenantA, retentionDays: 30, v2: false, sessions: new[] { Old("s1", 60) });
        // Override the default fresh-read: this session is locked by a V2 cascade.
        harness.SessionRepo.Setup(r => r.GetSessionAsync(TenantA, "s1"))
            .ReturnsAsync(new SessionSummary
            {
                TenantId = TenantA,
                SessionId = "s1",
                DeletionState = SessionDeletionState.Queued,
                PendingDeletionManifestId = "EXISTING-MANIFEST",
            });

        var result = await harness.Sut.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.SessionsSkipped);
        Assert.Equal(0, result.SessionsLegacyDeleted);
        // Critical assertion: NONE of the side-table delete helpers were invoked.
        harness.MaintenanceRepo.Verify(m => m.DeleteSessionEventsAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        harness.MaintenanceRepo.Verify(m => m.DeleteSessionRuleResultsAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        harness.MaintenanceRepo.Verify(m => m.DeleteSessionAppInstallSummariesAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        harness.SessionRepo.Verify(r => r.DeleteSessionAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_legacy_path_skips_when_session_already_removed_during_preread()
    {
        // Edge case: row disappeared between GetSessionsOlderThan and the pre-read. Treat as skip
        // (no side-table work needed); no error.
        var harness = new Harness();
        harness.WithTenant(TenantA, retentionDays: 30, v2: false, sessions: new[] { Old("s1", 60) });
        harness.SessionRepo.Setup(r => r.GetSessionAsync(TenantA, "s1")).ReturnsAsync((SessionSummary?)null);

        var result = await harness.Sut.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.SessionsSkipped);
        harness.MaintenanceRepo.Verify(m => m.DeleteSessionEventsAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // ────────────────────────────────────────────────────────────────────────── PR6 follow-up F2 ─

    [Fact]
    public async Task RunAsync_aborts_mid_loop_when_kill_switch_flips_on()
    {
        // PR6 follow-up F2: per-session kill-switch check halts the fanout immediately when the
        // emergency switch flips, instead of finishing the rest of this tenant's backlog.
        var harness = new Harness();
        var manySessions = new List<SessionSummary>();
        for (int i = 0; i < 5; i++) manySessions.Add(Summary(TenantA, $"s{i}"));
        harness.WithTenantOverride(TenantA, retentionDays: 30, v2: true, sessions: manySessions);

        // First 2 calls return false (kill-switch off), then true → fanout aborts at the 3rd iteration.
        var calls = 0;
        harness.AdminConfig.Setup(a => a.IsSessionDeletionKillSwitchActiveAsync())
            .Returns(() =>
            {
                calls++;
                // Sequence: tenant-entry probe (call 1, false), then per-session probes:
                // session 0 (call 2, false), session 1 (call 3, false), session 2 (call 4, TRUE — abort).
                return Task.FromResult(calls >= 4);
            });

        var result = await harness.Sut.RunAsync(CancellationToken.None);

        Assert.True(result.AbortedByKillSwitch);
        Assert.Equal(2, result.SessionsEnqueued); // only sessions 0 and 1 made it through
        // Confirm only the first two enqueues fired.
        harness.Enqueuer.Verify(e => e.EnqueueAsync(TenantA, "s0", It.IsAny<string>(), It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.Enqueuer.Verify(e => e.EnqueueAsync(TenantA, "s1", It.IsAny<string>(), It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.Enqueuer.Verify(e => e.EnqueueAsync(TenantA, "s2", It.IsAny<string>(), It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Enqueuer.Verify(e => e.EnqueueAsync(TenantA, "s4", It.IsAny<string>(), It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_aborts_at_tenant_boundary_when_kill_switch_flips_between_tenants()
    {
        // Kill-switch flips on between tenant A and tenant B → tenant B is never started.
        var harness = new Harness();
        harness.WithTenant(TenantA, retentionDays: 30, v2: true, sessions: new[] { Old("a1", 60) });
        harness.WithTenant(TenantB, retentionDays: 30, v2: true, sessions: new[] { Old("b1", 60) });

        var calls = 0;
        harness.AdminConfig.Setup(a => a.IsSessionDeletionKillSwitchActiveAsync())
            .Returns(() =>
            {
                calls++;
                // Tenant A: entry probe (call 1, false), session probe (call 2, false), enqueue runs.
                // Tenant B: entry probe (call 3, TRUE — abort).
                return Task.FromResult(calls >= 3);
            });

        var result = await harness.Sut.RunAsync(CancellationToken.None);

        Assert.True(result.AbortedByKillSwitch);
        Assert.Equal(1, result.TenantsProcessed);
        harness.Enqueuer.Verify(e => e.EnqueueAsync(TenantA, "a1", It.IsAny<string>(), It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.Enqueuer.Verify(e => e.EnqueueAsync(TenantB, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_counts_AlreadyInFlight_outcome_as_skip()
    {
        var harness = new Harness();
        harness.WithTenant(TenantA, retentionDays: 30, v2: true, sessions: new[] { Old("s1", 60) });
        // Another producer already claimed this session — fanout must not double-count.
        harness.Enqueuer.Setup(e => e.EnqueueAsync(TenantA, "s1", It.IsAny<string>(), It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionDeletionEnqueueResult
            {
                Outcome = SessionDeletionEnqueueOutcome.AlreadyInFlight,
                ManifestId = "OTHER-MANIFEST",
                ExistingState = SessionDeletionState.Running,
            });

        var result = await harness.Sut.RunAsync(CancellationToken.None);

        Assert.Equal(0, result.SessionsEnqueued);
        Assert.Equal(1, result.SessionsSkipped);
    }

    // ============================================================ Helpers ====

    private static SessionSummary Summary(string tenantId, string sessionId) =>
        new() { TenantId = tenantId, SessionId = sessionId };

    private static SessionSummary Old(string sessionId, int ageDays)
    {
        return new SessionSummary
        {
            SessionId = sessionId,
            StartedAt = DateTime.UtcNow.AddDays(-ageDays),
        };
    }

    // ============================================================ Harness ====

    private sealed class Harness
    {
        public Mock<IMaintenanceRepository> MaintenanceRepo { get; }
        public Mock<ISessionRepository> SessionRepo { get; }
        public Mock<TenantConfigurationService> TenantConfig { get; }
        public Mock<ISessionDeletionEnqueuer> Enqueuer { get; }
        public Mock<AdminConfigurationService> AdminConfig { get; }
        public SessionRetentionFanoutService Sut { get; }

        private readonly List<string> _tenantIds = new();

        public Harness()
        {
            MaintenanceRepo = new Mock<IMaintenanceRepository>();
            SessionRepo = new Mock<ISessionRepository>();
            var memCache = new MemoryCache(new MemoryCacheOptions());
            TenantConfig = new Mock<TenantConfigurationService>(
                Mock.Of<IConfigRepository>(), NullLogger<TenantConfigurationService>.Instance, memCache);
            Enqueuer = new Mock<ISessionDeletionEnqueuer>();

            // PR6 follow-up F1: legacy path now pre-reads the Sessions row via ISessionRepository
            // to check DeletionState. Default: row exists with DeletionState=None so legacy proceeds.
            SessionRepo.Setup(r => r.GetSessionAsync(It.IsAny<string>(), It.IsAny<string>()))
                .Returns<string, string>((tenant, sid) => Task.FromResult<SessionSummary?>(
                    new SessionSummary { TenantId = tenant, SessionId = sid, DeletionState = SessionDeletionState.None }));

            // PR6 follow-up F2: per-session kill-switch check inside the fanout loop.
            AdminConfig = new Mock<AdminConfigurationService>(
                Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance, memCache);
            AdminConfig.Setup(a => a.IsSessionDeletionKillSwitchActiveAsync()).ReturnsAsync(false);

            // Default: every enqueue succeeds.
            Enqueuer.Setup(e => e.EnqueueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SessionDeletionEnqueueResult
                {
                    Outcome = SessionDeletionEnqueueOutcome.Enqueued,
                    ManifestId = Guid.NewGuid().ToString("N"),
                });

            // Default: maintenance repo returns the registered tenants.
            MaintenanceRepo.Setup(m => m.GetAllTenantIdsAsync()).ReturnsAsync(() => new List<string>(_tenantIds));
            MaintenanceRepo.Setup(m => m.LogAuditEntryAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>()))
                .ReturnsAsync(true);

            // Use internal ctor to inject a no-op throttle so the rate-limit test runs in real-time
            // (50ms × 100 = 5s otherwise).
            Sut = new SessionRetentionFanoutService(
                MaintenanceRepo.Object, SessionRepo.Object, TenantConfig.Object, Enqueuer.Object,
                AdminConfig.Object,
                NullLogger<SessionRetentionFanoutService>.Instance,
                throttle: (_, _) => Task.CompletedTask);
        }

        public void WithTenant(string tenantId, int retentionDays, bool v2, SessionSummary[] sessions)
        {
            _tenantIds.Add(tenantId);
            TenantConfig.Setup(t => t.GetConfigurationAsync(tenantId))
                .ReturnsAsync(new TenantConfiguration { TenantId = tenantId, DataRetentionDays = retentionDays, EnableCascadeDeleteV2 = v2 });
            MaintenanceRepo.Setup(m => m.GetSessionsOlderThanAsync(tenantId, It.IsAny<DateTime>()))
                .ReturnsAsync(new List<SessionSummary>(WithTenantId(tenantId, sessions)));
        }

        public void WithTenantOverride(string tenantId, int retentionDays, bool v2, List<SessionSummary> sessions)
        {
            _tenantIds.Add(tenantId);
            TenantConfig.Setup(t => t.GetConfigurationAsync(tenantId))
                .ReturnsAsync(new TenantConfiguration { TenantId = tenantId, DataRetentionDays = retentionDays, EnableCascadeDeleteV2 = v2 });
            foreach (var s in sessions) s.TenantId = tenantId;
            MaintenanceRepo.Setup(m => m.GetSessionsOlderThanAsync(tenantId, It.IsAny<DateTime>())).ReturnsAsync(sessions);
        }

        private static IEnumerable<SessionSummary> WithTenantId(string tenantId, IEnumerable<SessionSummary> sessions)
        {
            foreach (var s in sessions) { s.TenantId = tenantId; yield return s; }
        }
    }
}
