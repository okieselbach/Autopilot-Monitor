using System.Linq;
using System.Net;
using AutopilotMonitor.Functions.Functions.Sessions;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.Deletion;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Thin HTTP-layer tests for <see cref="RestoreSessionFunction"/> (PR4b). The bulk of behaviour
/// lives in <see cref="SessionRestoreService"/> (covered by <c>SessionRestoreServiceTests</c>);
/// this file only verifies the layer-specific pieces: status mapping, route registration in
/// the policy catalog (GA-only enforcement), and body shape.
/// </summary>
public class RestoreSessionFunctionTests
{
    [Theory]
    [InlineData(SessionRestoreOutcome.Restored, HttpStatusCode.OK)]
    [InlineData(SessionRestoreOutcome.DryRunOk, HttpStatusCode.OK)]
    [InlineData(SessionRestoreOutcome.RejectManifestNotFound, HttpStatusCode.NotFound)]
    [InlineData(SessionRestoreOutcome.RejectAlreadyAtOriginalState, HttpStatusCode.Conflict)]
    [InlineData(SessionRestoreOutcome.RejectActiveCascade, HttpStatusCode.Conflict)]
    [InlineData(SessionRestoreOutcome.RejectManifestIdMismatch, HttpStatusCode.Conflict)]
    [InlineData(SessionRestoreOutcome.RejectCorruptState, HttpStatusCode.Conflict)]
    [InlineData(SessionRestoreOutcome.RejectManifestCorruption, HttpStatusCode.Conflict)]
    [InlineData(SessionRestoreOutcome.RejectCasConflictOnClear, HttpStatusCode.Conflict)]
    public void MapOutcomeToStatus_returns_expected_status(SessionRestoreOutcome outcome, HttpStatusCode expected)
    {
        Assert.Equal(expected, RestoreSessionFunction.MapOutcomeToStatus(outcome));
    }

    [Fact]
    public void Restore_route_is_registered_in_policy_catalog_as_GlobalAdminOnly()
    {
        // Per memory feedback_route_policy_catalog — every HTTP route MUST be registered in
        // EndpointAccessPolicyCatalog. An unregistered route fail-closes to 403. This test
        // guards the restore endpoint's GA-only enforcement.
        var entry = EndpointAccessPolicyCatalog.FindPolicy("POST", "admin/sessions/some-id/restore");

        Assert.NotNull(entry);
        Assert.Equal(EndpointPolicy.GlobalAdminOnly, entry!.Policy);
    }

    [Fact]
    public void Restore_route_is_NOT_registered_for_non_POST_methods()
    {
        // GET to the restore route should not match the POST registration.
        var getEntry = EndpointAccessPolicyCatalog.FindPolicy("GET", "admin/sessions/some-id/restore");
        Assert.Null(getEntry);

        var putEntry = EndpointAccessPolicyCatalog.FindPolicy("PUT", "admin/sessions/some-id/restore");
        Assert.Null(putEntry);
    }
}
