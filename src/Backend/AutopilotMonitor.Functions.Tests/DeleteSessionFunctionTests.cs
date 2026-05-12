using System.Net;
using System.Reflection;
using AutopilotMonitor.Functions.Functions.Sessions;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.Deletion;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Thin HTTP-layer tests for <see cref="DeleteSessionFunction"/> (plan §5 PR5). Mirrors the
/// <see cref="RestoreSessionFunctionTests"/> pattern: verifies the policy-catalog registration
/// and the public status-mapping / body-shape helpers without touching <c>HttpRequestData</c>.
/// <para>
/// The end-to-end coordination (kill-switch → 503, locked → 409, flag toggle → V2/legacy)
/// is covered by the producer test suite (PR3) for the V2 outcomes, the worker tests (PR4)
/// for cascade behaviour, and the §18.4 internal-tenant integration run before any tenant
/// flag flip. There is intentionally NO HttpRequestData mock here — the production code is
/// the same shell wrapped around <see cref="DeleteSessionFunction.MapEnqueueOutcomeToStatus"/>.
/// </para>
/// </summary>
public class DeleteSessionFunctionTests
{
    private const string SessionId  = "22222222-2222-2222-2222-222222222222";
    private const string ManifestId = "01J0123456789ABCDEFGHIJKLM";

    // ── Policy catalog ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Delete_route_is_registered_in_policy_catalog_as_TenantAdminOrGA()
    {
        // Memory feedback_route_policy_catalog: every HTTP route MUST be registered in
        // EndpointAccessPolicyCatalog. Unregistered routes fail-closed → 403. PR5 keeps the
        // legacy policy unchanged (V2 is a body/path-dispatch detail, not an authorization one).
        var entry = EndpointAccessPolicyCatalog.FindPolicy("DELETE", "sessions/" + SessionId);

        Assert.NotNull(entry);
        Assert.Equal(EndpointPolicy.TenantAdminOrGA, entry!.Policy);
    }

    // ── MapEnqueueOutcomeToStatus ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SessionDeletionEnqueueOutcome.Enqueued,         HttpStatusCode.Accepted)]
    [InlineData(SessionDeletionEnqueueOutcome.AlreadyInFlight,  HttpStatusCode.Conflict)]
    [InlineData(SessionDeletionEnqueueOutcome.Poisoned,         HttpStatusCode.Conflict)]
    [InlineData(SessionDeletionEnqueueOutcome.KillSwitchActive, HttpStatusCode.ServiceUnavailable)]
    [InlineData(SessionDeletionEnqueueOutcome.CasExhausted,     HttpStatusCode.ServiceUnavailable)]
    [InlineData(SessionDeletionEnqueueOutcome.SessionNotFound,  HttpStatusCode.NotFound)]
    public void MapEnqueueOutcomeToStatus_returns_expected_status(
        SessionDeletionEnqueueOutcome outcome, HttpStatusCode expected)
    {
        Assert.Equal(expected, DeleteSessionFunction.MapEnqueueOutcomeToStatus(outcome));
    }

    [Fact]
    public void MapEnqueueOutcomeToStatus_falls_back_to_500_on_unknown_outcome()
    {
        // Defensive: a new outcome value added later without updating the mapping must surface
        // as a 500 (visible bug) instead of silently masquerading as 200/202. Cast to bypass
        // the enum bounds check — represents a hypothetical future enum addition.
        var bogus = (SessionDeletionEnqueueOutcome)999;
        Assert.Equal(HttpStatusCode.InternalServerError, DeleteSessionFunction.MapEnqueueOutcomeToStatus(bogus));
    }

    // ── BuildV2ResponseBody ───────────────────────────────────────────────────────────────

    [Fact]
    public void BuildV2ResponseBody_Enqueued_carries_manifestId_and_queued_status()
    {
        var result = new SessionDeletionEnqueueResult
        {
            Outcome = SessionDeletionEnqueueOutcome.Enqueued,
            ManifestId = ManifestId,
        };

        var body = DeleteSessionFunction.BuildV2ResponseBody(result, SessionId);

        AssertProperty(body, "success", true);
        AssertProperty(body, "status", "queued");
        AssertProperty(body, "manifestId", ManifestId);
    }

    [Fact]
    public void BuildV2ResponseBody_AlreadyInFlight_carries_state_and_manifestId_with_hint()
    {
        var result = new SessionDeletionEnqueueResult
        {
            Outcome = SessionDeletionEnqueueOutcome.AlreadyInFlight,
            ManifestId = ManifestId,
            ExistingState = "Running",
        };

        var body = DeleteSessionFunction.BuildV2ResponseBody(result, SessionId);

        AssertProperty(body, "success", false);
        AssertProperty(body, "manifestId", ManifestId);
        AssertProperty(body, "deletionState", "Running");
        AssertProperty(body, "hint", "cascade_already_in_flight");
    }

    [Fact]
    public void BuildV2ResponseBody_Poisoned_hints_at_restore_endpoint()
    {
        // Plan §13: the only recovery path from Poisoned is POST /restore. The HTTP body
        // must say so explicitly so the UI/operator does not loop on the delete endpoint.
        var result = new SessionDeletionEnqueueResult
        {
            Outcome = SessionDeletionEnqueueOutcome.Poisoned,
            ManifestId = ManifestId,
            ExistingState = "Poisoned",
        };

        var body = DeleteSessionFunction.BuildV2ResponseBody(result, SessionId);

        AssertProperty(body, "hint", "cascade_poisoned_use_restore");
        AssertProperty(body, "manifestId", ManifestId);
        // The message string must mention the restore endpoint so the UI surfaces it verbatim.
        var message = GetProperty<string>(body, "message");
        Assert.Contains("/restore", message);
    }

    [Fact]
    public void BuildV2ResponseBody_KillSwitchActive_does_not_leak_manifestId()
    {
        // Race: kill-switch flipped between step-1 admin check and the producer's CAS read.
        // The producer never built a manifest, so the body must NOT carry a manifestId at all
        // (avoid the UI rendering a "track this cascade" link to a non-existent manifest).
        var result = new SessionDeletionEnqueueResult
        {
            Outcome = SessionDeletionEnqueueOutcome.KillSwitchActive,
        };

        var body = DeleteSessionFunction.BuildV2ResponseBody(result, SessionId);

        AssertProperty(body, "hint", "kill_switch_active");
        Assert.False(HasProperty(body, "manifestId"));
    }

    // ── Anonymous-object reflection helpers ───────────────────────────────────────────────

    private static void AssertProperty(object body, string propertyName, object? expected)
    {
        var prop = body.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(prop);
        Assert.Equal(expected, prop!.GetValue(body));
    }

    private static T GetProperty<T>(object body, string propertyName)
    {
        var prop = body.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(prop);
        var value = prop!.GetValue(body);
        return (T)value!;
    }

    private static bool HasProperty(object body, string propertyName) =>
        body.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance) != null;
}
