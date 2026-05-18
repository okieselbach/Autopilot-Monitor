using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models.Graph;

namespace AutopilotMonitor.Functions.Services.GraphResolution;

/// <summary>
/// Resolves Intune script (Platform + Remediation) display names for a tenant. Encapsulates
/// the gate (does the SP have the required Graph permission?), the cache (have we seen this
/// script recently?), the Graph fetch (list-full-pull or per-ID fallback), and the negative-
/// cache write for 404s.
/// <para>
/// Returns a dictionary where the value for any unresolved ref is <c>null</c>. Never throws
/// to the caller — Graph timeouts / 429s / 5xx degrade to "no name" so the UI can still
/// render IDs. Cache failures behave the same way.
/// </para>
/// </summary>
public interface IScriptDisplayNameResolver
{
    Task<IReadOnlyDictionary<ScriptRef, string?>> ResolveAsync(
        string tenantId,
        IReadOnlyCollection<ScriptRef> refs,
        CancellationToken ct = default);
}
