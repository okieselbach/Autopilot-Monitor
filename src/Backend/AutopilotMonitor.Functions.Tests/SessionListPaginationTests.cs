using System.Collections.Generic;
using System.Collections.Specialized;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// PR 5 (mcp-pagination-rollout) — pagination wiring on
/// <c>GET /api/sessions</c> + <c>GET /api/global/sessions</c>. Replaces the
/// legacy <c>?limit=</c> + <c>?cursor=</c> wire shape with the rollout plan's
/// <c>?pageSize=</c> + <c>?continuation=</c> + <c>nextLink</c> contract.
/// </summary>
public class SessionListPaginationTests
{
    private const string TenantA = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string TenantB = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    // ────────── ParseQuery ──────────────────────────────────────────────────

    [Fact]
    public void ParseQuery_with_no_params_returns_default_pageSize()
    {
        var parsed = SessionListPagination.ParseQuery(new NameValueCollection(), acceptFilterTenantId: false);

        Assert.Null(parsed.Error);
        Assert.Equal(SessionListPagination.DefaultPageSize, parsed.PageSize);
        Assert.Null(parsed.Continuation);
        Assert.Null(parsed.Days);
        Assert.Null(parsed.FilterTenantId);
    }

    [Fact]
    public void ParseQuery_honours_pageSize_and_continuation()
    {
        var parsed = SessionListPagination.ParseQuery(new NameValueCollection
        {
            { "pageSize", "10" },
            { "continuation", "ENCODED" },
        }, acceptFilterTenantId: false);

        Assert.Equal(10, parsed.PageSize);
        Assert.Equal("ENCODED", parsed.Continuation);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1001")]
    [InlineData("banana")]
    public void ParseQuery_rejects_invalid_pageSize(string raw)
    {
        var parsed = SessionListPagination.ParseQuery(
            new NameValueCollection { { "pageSize", raw } },
            acceptFilterTenantId: false);
        Assert.NotNull(parsed.Error);
    }

    [Fact]
    public void ParseQuery_carries_days_filter()
    {
        var parsed = SessionListPagination.ParseQuery(
            new NameValueCollection { { "days", "30" } },
            acceptFilterTenantId: false);
        Assert.Equal(30, parsed.Days);
    }

    [Fact]
    public void ParseQuery_rejects_invalid_days()
    {
        var parsed = SessionListPagination.ParseQuery(
            new NameValueCollection { { "days", "0" } },
            acceptFilterTenantId: false);
        Assert.NotNull(parsed.Error);
    }

    [Fact]
    public void ParseQuery_ignores_filter_tenantId_when_not_accepted()
    {
        // The tenant-scoped endpoint must never honour ?tenantId= in the query
        // — that's an authorization vector. acceptFilterTenantId=false skips it.
        var parsed = SessionListPagination.ParseQuery(
            new NameValueCollection { { "tenantId", TenantB } },
            acceptFilterTenantId: false);
        Assert.Null(parsed.FilterTenantId);
    }

    [Fact]
    public void ParseQuery_carries_filter_tenantId_when_accepted()
    {
        var parsed = SessionListPagination.ParseQuery(
            new NameValueCollection { { "tenantId", TenantB } },
            acceptFilterTenantId: true);
        Assert.Equal(TenantB, parsed.FilterTenantId);
    }

    // ────────── Token round-trip + cross-binding rejections ─────────────────

    [Fact]
    public void TryAcceptContinuation_round_trips_for_matching_caller()
    {
        var fp = SessionListPagination.Fingerprint("sessions:tenant", TenantA, days: 30);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fp);

        var ok = SessionListPagination.TryAcceptContinuation(
            encoded, "sessions:tenant", TenantA, days: 30, filterTenantId: null,
            out var azure, out var reason);

        Assert.True(ok);
        Assert.Null(reason);
        Assert.Equal("rawAzure", azure);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_for_a_different_caller()
    {
        var fp = SessionListPagination.Fingerprint("sessions:tenant", TenantA, days: null);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fp);

        var ok = SessionListPagination.TryAcceptContinuation(
            encoded, "sessions:tenant", TenantB, days: null, filterTenantId: null,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("cross_tenant", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_when_days_changes()
    {
        var fp30 = SessionListPagination.Fingerprint("sessions:tenant", TenantA, days: 30);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fp30);

        var ok = SessionListPagination.TryAcceptContinuation(
            encoded, "sessions:tenant", TenantA, days: 90, filterTenantId: null,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_when_scope_changes()
    {
        // Token from /api/sessions (tenant scope) replayed into /api/global/sessions
        // (global scope) — must reject.
        var fpTenant = SessionListPagination.Fingerprint("sessions:tenant", TenantA, days: null);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fpTenant);

        var ok = SessionListPagination.TryAcceptContinuation(
            encoded, "sessions:global", TenantA, days: null, filterTenantId: null,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_when_filterTenantId_changes_on_global()
    {
        // Global endpoint with ?tenantId=A then ?tenantId=B → token from page 1
        // must not seek into a different tenant's data on page 2.
        var fpA = SessionListPagination.Fingerprint("sessions:global", TenantA, days: null, filterTenantId: TenantA);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fpA);

        var ok = SessionListPagination.TryAcceptContinuation(
            encoded, "sessions:global", TenantA, days: null, filterTenantId: TenantB,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    // ────────── BuildNextLink ───────────────────────────────────────────────

    [Fact]
    public void BuildNextLink_includes_pageSize_continuation_days_filterTenantId()
    {
        var link = SessionListPagination.BuildNextLink(
            "/api/global/sessions", 10, "TOKEN+/=", days: 30, filterTenantId: TenantA);

        Assert.StartsWith("/api/global/sessions?", link);
        Assert.Contains("pageSize=10", link);
        Assert.Contains("continuation=TOKEN%2B%2F%3D", link);
        Assert.Contains("days=30", link);
        Assert.Contains($"tenantId={TenantA}", link);
    }

    [Fact]
    public void BuildNextLink_omits_optional_params_when_unset()
    {
        var link = SessionListPagination.BuildNextLink(
            "/api/sessions", 50, "abc", days: null, filterTenantId: null);

        Assert.Contains("pageSize=50", link);
        Assert.Contains("continuation=abc", link);
        Assert.DoesNotContain("days=", link);
        Assert.DoesNotContain("tenantId=", link);
    }

    // ────────── Repo-contract: drain MUST keep paginating with days set ─────

    [Fact]
    public async Task ConsumeUntilAbsent_drains_all_sessions_with_days_via_continuation_loop()
    {
        // Regression guard for the storage-layer short-circuit that previously
        // returned NextRawToken=null whenever `days` was set, capping callers at
        // ~10k sessions silently. After the fix, the days cutoff is applied as a
        // RowKey filter and pagination mechanics are independent of the window —
        // so a tenant with thousands of in-window sessions remains fully reachable
        // across multiple pages, exactly like the no-days case.
        const int totalSessions = 1500;
        const int pageSize = 200;
        const int days = 30;
        var pages = SplitSessionPages(totalSessions, pageSize);

        var repo = new Mock<AutopilotMonitor.Shared.DataAccess.ISessionRepository>(MockBehavior.Strict);
        var callIndex = 0;
        repo
            .Setup(r => r.GetSessionsPageAsync(TenantA, days, pageSize, It.IsAny<string?>()))
            .ReturnsAsync(() => pages[callIndex++]);

        var collected = new List<SessionSummary>();
        string? continuation = null;
        var loopGuard = 0;
        while (true)
        {
            var page = await repo.Object.GetSessionsPageAsync(TenantA, days, pageSize, continuation);
            collected.AddRange(page.Items);
            if (string.IsNullOrEmpty(page.NextRawToken)) break;
            continuation = page.NextRawToken;
            if (++loopGuard > 50) Assert.Fail("Pagination loop did not terminate within 50 iterations");
        }

        Assert.Equal(totalSessions, collected.Count);
        // SessionIds must be unique — no session emitted twice across pages.
        var ids = new HashSet<string>(System.Linq.Enumerable.Select(collected, s => s.SessionId));
        Assert.Equal(totalSessions, ids.Count);
    }

    private static List<RawPage<SessionSummary>> SplitSessionPages(int total, int pageSize)
    {
        var result = new List<RawPage<SessionSummary>>();
        var emitted = 0;
        while (emitted < total)
        {
            var take = System.Math.Min(pageSize, total - emitted);
            var batch = new List<SessionSummary>(take);
            for (int i = 0; i < take; i++)
            {
                batch.Add(new SessionSummary
                {
                    TenantId = TenantA,
                    SessionId = $"sess-{emitted + i:D6}",
                    StartedAt = System.DateTime.UtcNow.AddMinutes(-(emitted + i)),
                });
            }
            emitted += take;
            var hasMore = emitted < total;
            result.Add(new RawPage<SessionSummary>(batch, hasMore ? $"raw-cursor-{result.Count + 1}" : null));
        }
        return result;
    }
}
