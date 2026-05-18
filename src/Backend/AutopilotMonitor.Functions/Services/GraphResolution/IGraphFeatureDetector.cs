using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AutopilotMonitor.Functions.Services.GraphResolution;

/// <summary>
/// Decides whether a tenant has granted the Autopilot Monitor service principal a given
/// Microsoft Graph application permission. The truth source is the <c>roles</c> claim
/// of the access token issued to the SP — which transparently reflects both
/// manifest-consented permissions AND tenant-side <c>appRoleAssignment</c> grants made
/// by the customer-side <c>Grant-AutopilotMonitorAddOn.ps1</c> script.
/// <para>
/// Implementations cache the parsed roles per tenant for the token lifetime (capped at
/// ~55 min). Callers should treat the result as "best known"; after a fresh grant the
/// customer-side script must trigger <see cref="InvalidateTenant"/> via the
/// <c>/graph-permissions/refresh</c> endpoint, or wait for natural cache expiry.
/// </para>
/// </summary>
public interface IGraphFeatureDetector
{
    /// <summary>
    /// True when the SP's currently-cached access token for <paramref name="tenantId"/>
    /// carries the named <paramref name="graphAppRole"/> in its <c>roles</c> claim.
    /// Returns false on token-acquire failure, missing roles claim, or any transient error
    /// — callers must degrade gracefully, never throw. Optional-feature callers should
    /// supply a <paramref name="ct"/> with a tight budget so a slow Graph cannot block the UI.
    /// </summary>
    Task<bool> HasPermissionAsync(string tenantId, string graphAppRole, CancellationToken ct = default);

    /// <summary>
    /// Same data as <see cref="HasPermissionAsync"/>, but returns the full set of granted
    /// Graph application roles so the Admin UI can render a capability matrix.
    /// Returns an empty set when no token can be acquired.
    /// </summary>
    Task<IReadOnlySet<string>> GetGrantedRolesAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Returns the cached access token alongside the parsed roles. Used by the script
    /// display-name resolver to avoid double-fetching the token. Returns null when no
    /// token can be acquired (no consent, transient error). Caller MUST NOT log or persist
    /// the access token.
    /// </summary>
    Task<GraphTenantTokenContext?> TryGetTokenContextAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Returns the granted-roles snapshot AND the transient flag together — the only way
    /// for an admin-UI caller to distinguish "tenant has nothing granted" (permanent
    /// zero-roles result) from "we couldn't decide right now" (timeout, retry budget
    /// exhausted, Graph 5xx). The status endpoint surfaces the difference to the UI so
    /// it can render "Try again" instead of "0 of 0 granted".
    /// </summary>
    Task<GraphPermissionSnapshot> GetSnapshotAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Clears the cached entry for <paramref name="tenantId"/> so the next call re-acquires.
    /// Called after the customer-side grant script reports success.
    /// </summary>
    void InvalidateTenant(string tenantId);
}

/// <summary>
/// Status-endpoint-facing view of a tenant's Graph permission state. <see cref="IsTransient"/>
/// is true when the underlying token acquire timed out or hit a retryable failure; callers
/// should NOT cache "no permissions" in that case and should let the user retry.
/// </summary>
public sealed class GraphPermissionSnapshot
{
    public static GraphPermissionSnapshot Empty { get; } = new GraphPermissionSnapshot
    {
        GrantedRoles = new System.Collections.Generic.HashSet<string>(),
        IsTransient = false,
    };

    public IReadOnlySet<string> GrantedRoles { get; init; } = new System.Collections.Generic.HashSet<string>();

    /// <summary>
    /// True when the result is not authoritative — token-acquire timeout, network failure,
    /// or our internal budget elapsed. Distinguished from "tenant has not granted any
    /// optional permissions" (transient false + empty <see cref="GrantedRoles"/>).
    /// </summary>
    public bool IsTransient { get; init; }
}

/// <summary>
/// Cached "what does the SP look like in this tenant right now" bundle.
/// Held in-memory only; never persisted.
/// </summary>
public sealed class GraphTenantTokenContext
{
    public string TenantId { get; init; } = string.Empty;
    public string AccessToken { get; init; } = string.Empty;
    public IReadOnlySet<string> GrantedRoles { get; init; } = new HashSet<string>();
    public System.DateTimeOffset ExpiresAt { get; init; }
}
