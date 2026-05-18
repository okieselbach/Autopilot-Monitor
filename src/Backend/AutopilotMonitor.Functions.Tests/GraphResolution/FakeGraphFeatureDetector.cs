using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.GraphResolution;

namespace AutopilotMonitor.Functions.Tests.GraphResolution;

/// <summary>In-memory <see cref="IGraphFeatureDetector"/> fake for resolver tests.</summary>
internal sealed class FakeGraphFeatureDetector : IGraphFeatureDetector
{
    public Dictionary<string, GraphTenantTokenContext?> ContextByTenant { get; } = new();
    public List<string> InvalidateCalls { get; } = new();

    public Task<bool> HasPermissionAsync(string tenantId, string graphAppRole, CancellationToken ct = default)
    {
        ContextByTenant.TryGetValue(tenantId, out var ctx);
        return Task.FromResult(ctx != null && ctx.GrantedRoles.Contains(graphAppRole));
    }

    public Task<IReadOnlySet<string>> GetGrantedRolesAsync(string tenantId, CancellationToken ct = default)
    {
        ContextByTenant.TryGetValue(tenantId, out var ctx);
        return Task.FromResult<IReadOnlySet<string>>(
            ctx?.GrantedRoles ?? (IReadOnlySet<string>)new HashSet<string>());
    }

    public Task<GraphTenantTokenContext?> TryGetTokenContextAsync(string tenantId, CancellationToken ct = default)
    {
        ContextByTenant.TryGetValue(tenantId, out var ctx);
        return Task.FromResult(ctx);
    }

    /// <summary>When set for a tenant, <see cref="GetSnapshotAsync"/> reports it as transient.</summary>
    public HashSet<string> TransientTenants { get; } = new();

    public Task<GraphPermissionSnapshot> GetSnapshotAsync(string tenantId, CancellationToken ct = default)
    {
        if (TransientTenants.Contains(tenantId))
        {
            return Task.FromResult(new GraphPermissionSnapshot { IsTransient = true });
        }
        ContextByTenant.TryGetValue(tenantId, out var ctx);
        return Task.FromResult(new GraphPermissionSnapshot
        {
            GrantedRoles = ctx?.GrantedRoles ?? (IReadOnlySet<string>)new HashSet<string>(),
            IsTransient = false,
        });
    }

    public void InvalidateTenant(string tenantId) => InvalidateCalls.Add(tenantId);
}
