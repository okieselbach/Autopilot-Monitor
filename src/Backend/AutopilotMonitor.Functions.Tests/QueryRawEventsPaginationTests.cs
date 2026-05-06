using System.Collections.Specialized;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Shared.Pagination;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// PR 6 (mcp-pagination-rollout) — pagination wiring on
/// <c>GET /api/raw/events</c> + <c>GET /api/global/raw/events</c>. Replaces the
/// legacy <c>?limit=</c> wire shape and the hard-coded <c>limit:20</c>
/// EventTypeIndex lookup that silently capped recall on large tenants.
/// Includes the single-session path, which paginates session events so
/// sessions with more matching events than <c>pageSize</c> stay reachable
/// across multiple <c>nextLink</c> follow-ups.
/// </summary>
public class QueryRawEventsPaginationTests
{
    private const string TenantA = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string TenantB = "b2c3d4e5-f6a7-8901-bcde-f12345678901";
    private const string FilterA = "00000000-1111-2222-3333-444444444444";
    private const string SessionA = "11111111-2222-3333-4444-555555555555";
    private const string SessionB = "99999999-8888-7777-6666-555555555555";

    // ────────── ParsePagination ─────────────────────────────────────────────

    [Fact]
    public void ParsePagination_with_no_params_returns_default_pageSize()
    {
        var parsed = QueryRawEventsPagination.ParsePagination(new NameValueCollection());

        Assert.Null(parsed.Error);
        Assert.Equal(QueryRawEventsPagination.DefaultPageSize, parsed.PageSize);
        Assert.Null(parsed.Continuation);
    }

    [Fact]
    public void ParsePagination_honours_pageSize_and_continuation()
    {
        var parsed = QueryRawEventsPagination.ParsePagination(new NameValueCollection
        {
            { "pageSize", "500" },
            { "continuation", "ENCODED" },
        });

        Assert.Equal(500, parsed.PageSize);
        Assert.Equal("ENCODED", parsed.Continuation);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1001")]
    [InlineData("banana")]
    public void ParsePagination_rejects_invalid_pageSize(string raw)
    {
        var parsed = QueryRawEventsPagination.ParsePagination(new NameValueCollection { { "pageSize", raw } });
        Assert.NotNull(parsed.Error);
    }

    // ────────── Cross-session token round-trip + cross-binding rejections ───

    [Fact]
    public void TryAcceptContinuation_round_trips_for_matching_filters()
    {
        var fp = QueryRawEventsPagination.Fingerprint(
            scope: "raw-events:tenant",
            callerTenantId: TenantA,
            filterTenantId: null,
            sessionId: null,
            eventType: "app_install_failed",
            source: "Teams",
            severity: "Error",
            startedAfter: null,
            startedBefore: null);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fp);

        var ok = QueryRawEventsPagination.TryAcceptContinuation(
            raw: encoded,
            scope: "raw-events:tenant",
            callerTenantId: TenantA,
            filterTenantId: null,
            sessionId: null,
            eventType: "app_install_failed",
            source: "Teams",
            severity: "Error",
            startedAfter: null,
            startedBefore: null,
            out var azure, out var reason);

        Assert.True(ok);
        Assert.Null(reason);
        Assert.Equal("rawAzure", azure);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_for_a_different_caller()
    {
        var fp = QueryRawEventsPagination.Fingerprint(
            scope: "raw-events:tenant", callerTenantId: TenantA,
            filterTenantId: null, sessionId: null,
            eventType: "app_install_failed", source: null, severity: null,
            startedAfter: null, startedBefore: null);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fp);

        var ok = QueryRawEventsPagination.TryAcceptContinuation(
            raw: encoded,
            scope: "raw-events:tenant", callerTenantId: TenantB,
            filterTenantId: null, sessionId: null,
            eventType: "app_install_failed", source: null, severity: null,
            startedAfter: null, startedBefore: null,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("cross_tenant", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_when_eventType_changes()
    {
        var fp = QueryRawEventsPagination.Fingerprint(
            scope: "raw-events:tenant", callerTenantId: TenantA,
            filterTenantId: null, sessionId: null,
            eventType: "app_install_failed", source: null, severity: null,
            startedAfter: null, startedBefore: null);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fp);

        var ok = QueryRawEventsPagination.TryAcceptContinuation(
            raw: encoded,
            scope: "raw-events:tenant", callerTenantId: TenantA,
            filterTenantId: null, sessionId: null,
            eventType: "error_detected", source: null, severity: null,
            startedAfter: null, startedBefore: null,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_when_severity_changes()
    {
        var fp = QueryRawEventsPagination.Fingerprint(
            scope: "raw-events:tenant", callerTenantId: TenantA,
            filterTenantId: null, sessionId: null,
            eventType: "app_install_failed", source: null, severity: "Error",
            startedAfter: null, startedBefore: null);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fp);

        var ok = QueryRawEventsPagination.TryAcceptContinuation(
            raw: encoded,
            scope: "raw-events:tenant", callerTenantId: TenantA,
            filterTenantId: null, sessionId: null,
            eventType: "app_install_failed", source: null, severity: "Critical",
            startedAfter: null, startedBefore: null,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_when_source_changes()
    {
        var fp = QueryRawEventsPagination.Fingerprint(
            scope: "raw-events:tenant", callerTenantId: TenantA,
            filterTenantId: null, sessionId: null,
            eventType: "app_install_failed", source: "Teams", severity: null,
            startedAfter: null, startedBefore: null);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fp);

        var ok = QueryRawEventsPagination.TryAcceptContinuation(
            raw: encoded,
            scope: "raw-events:tenant", callerTenantId: TenantA,
            filterTenantId: null, sessionId: null,
            eventType: "app_install_failed", source: "Office", severity: null,
            startedAfter: null, startedBefore: null,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_when_date_window_changes()
    {
        var fp = QueryRawEventsPagination.Fingerprint(
            scope: "raw-events:tenant", callerTenantId: TenantA,
            filterTenantId: null, sessionId: null,
            eventType: "app_install_failed", source: null, severity: null,
            startedAfter: "2026-04-01T00:00:00Z", startedBefore: null);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fp);

        var ok = QueryRawEventsPagination.TryAcceptContinuation(
            raw: encoded,
            scope: "raw-events:tenant", callerTenantId: TenantA,
            filterTenantId: null, sessionId: null,
            eventType: "app_install_failed", source: null, severity: null,
            startedAfter: "2026-05-01T00:00:00Z", startedBefore: null,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_when_scope_changes()
    {
        var fpTenant = QueryRawEventsPagination.Fingerprint(
            scope: "raw-events:tenant", callerTenantId: TenantA,
            filterTenantId: null, sessionId: null,
            eventType: "app_install_failed", source: null, severity: null,
            startedAfter: null, startedBefore: null);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fpTenant);

        var ok = QueryRawEventsPagination.TryAcceptContinuation(
            raw: encoded,
            scope: "raw-events:global", callerTenantId: TenantA,
            filterTenantId: null, sessionId: null,
            eventType: "app_install_failed", source: null, severity: null,
            startedAfter: null, startedBefore: null,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_when_global_filterTenantId_changes()
    {
        var fpA = QueryRawEventsPagination.Fingerprint(
            scope: "raw-events:global", callerTenantId: TenantA,
            filterTenantId: FilterA, sessionId: null,
            eventType: "app_install_failed", source: null, severity: null,
            startedAfter: null, startedBefore: null);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fpA);

        var ok = QueryRawEventsPagination.TryAcceptContinuation(
            raw: encoded,
            scope: "raw-events:global", callerTenantId: TenantA,
            filterTenantId: "99999999-9999-9999-9999-999999999999",
            sessionId: null,
            eventType: "app_install_failed", source: null, severity: null,
            startedAfter: null, startedBefore: null,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    // ────────── Single-session path: round-trip + cross-binding ─────────────

    [Fact]
    public void TryAcceptContinuation_round_trips_on_single_session_path()
    {
        var fp = QueryRawEventsPagination.Fingerprint(
            scope: "raw-events:tenant", callerTenantId: TenantA,
            filterTenantId: null, sessionId: SessionA,
            eventType: null, source: null, severity: null,
            startedAfter: null, startedBefore: null);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fp);

        var ok = QueryRawEventsPagination.TryAcceptContinuation(
            raw: encoded,
            scope: "raw-events:tenant", callerTenantId: TenantA,
            filterTenantId: null, sessionId: SessionA,
            eventType: null, source: null, severity: null,
            startedAfter: null, startedBefore: null,
            out var azure, out var reason);

        Assert.True(ok);
        Assert.Null(reason);
        Assert.Equal("rawAzure", azure);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_replayed_for_a_different_session()
    {
        // Cursor issued for sessionA must NOT be accepted when caller switches to
        // sessionB — otherwise events from sessionB could be served against the
        // /api/raw/events?sessionId= API contract.
        var fp = QueryRawEventsPagination.Fingerprint(
            scope: "raw-events:tenant", callerTenantId: TenantA,
            filterTenantId: null, sessionId: SessionA,
            eventType: null, source: null, severity: null,
            startedAfter: null, startedBefore: null);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fp);

        var ok = QueryRawEventsPagination.TryAcceptContinuation(
            raw: encoded,
            scope: "raw-events:tenant", callerTenantId: TenantA,
            filterTenantId: null, sessionId: SessionB,
            eventType: null, source: null, severity: null,
            startedAfter: null, startedBefore: null,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_single_session_token_replayed_into_cross_session_path()
    {
        // Token bound to sessionId = SessionA must not silently work when the
        // caller drops sessionId and queries cross-session by eventType only.
        var fp = QueryRawEventsPagination.Fingerprint(
            scope: "raw-events:tenant", callerTenantId: TenantA,
            filterTenantId: null, sessionId: SessionA,
            eventType: null, source: null, severity: null,
            startedAfter: null, startedBefore: null);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fp);

        var ok = QueryRawEventsPagination.TryAcceptContinuation(
            raw: encoded,
            scope: "raw-events:tenant", callerTenantId: TenantA,
            filterTenantId: null, sessionId: null,
            eventType: "app_install_failed", source: null, severity: null,
            startedAfter: null, startedBefore: null,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_single_session_token_when_severity_filter_changes()
    {
        var fp = QueryRawEventsPagination.Fingerprint(
            scope: "raw-events:tenant", callerTenantId: TenantA,
            filterTenantId: null, sessionId: SessionA,
            eventType: null, source: null, severity: "Error",
            startedAfter: null, startedBefore: null);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fp);

        var ok = QueryRawEventsPagination.TryAcceptContinuation(
            raw: encoded,
            scope: "raw-events:tenant", callerTenantId: TenantA,
            filterTenantId: null, sessionId: SessionA,
            eventType: null, source: null, severity: "Critical",
            startedAfter: null, startedBefore: null,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    // ────────── BuildNextLink ───────────────────────────────────────────────

    [Fact]
    public void BuildNextLink_echoes_filter_params_and_drops_legacy_keys()
    {
        var query = new NameValueCollection
        {
            { "eventType", "app_install_failed" },
            { "severity", "Error" },
            { "source", "Teams" },
            { "startedAfter", "2026-04-01T00:00:00Z" },
            { "limit", "100" },           // legacy → must be dropped
            { "pageSize", "999" },        // owned by new contract — overwritten
            { "continuation", "STALE" },  // owned by new contract — overwritten
        };

        var link = QueryRawEventsPagination.BuildNextLink(
            "/api/raw/events", 200, "FRESH+/=", query);

        Assert.StartsWith("/api/raw/events?", link);
        Assert.Contains("pageSize=200", link);
        Assert.Contains("continuation=FRESH%2B%2F%3D", link);
        Assert.Contains("eventType=app_install_failed", link);
        Assert.Contains("severity=Error", link);
        Assert.Contains("source=Teams", link);
        Assert.Contains("startedAfter=2026-04-01T00%3A00%3A00Z", link);
        Assert.DoesNotContain("limit=", link);
        Assert.DoesNotContain("STALE", link);
    }

    [Fact]
    public void BuildNextLink_preserves_sessionId_on_single_session_path()
    {
        // Single-session path must echo sessionId so the bookmark survives
        // round-trips and the AI/UI doesn't lose context across pages.
        var query = new NameValueCollection
        {
            { "sessionId", SessionA },
            { "tenantId", TenantA },
        };

        var link = QueryRawEventsPagination.BuildNextLink(
            "/api/raw/events", 200, "TOKEN", query);

        Assert.Contains($"sessionId={SessionA}", link);
        Assert.Contains($"tenantId={TenantA}", link);
    }
}
