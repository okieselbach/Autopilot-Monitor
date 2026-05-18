using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Graph;

namespace AutopilotMonitor.Functions.Tests.GraphResolution;

/// <summary>
/// In-memory <see cref="IScriptNameCacheRepository"/> fake. Reusable across PR-B (cleanup
/// function tests) and PR-C (resolver integration tests).
/// </summary>
internal sealed class FakeScriptNameCacheRepository : IScriptNameCacheRepository
{
    public Dictionary<(string Tenant, ScriptKind Kind, string Id), ScriptDisplayNameEntry> Entries { get; } = new();
    public Dictionary<(string Tenant, ScriptKind Kind), ScriptNameCacheMeta> Meta { get; } = new();
    public List<(string Tenant, ScriptKind Kind, string Id)> DeleteCalls { get; } = new();

    /// <summary>If non-null, the next Delete call throws this exception (one-shot).</summary>
    public Exception? NextDeleteThrows { get; set; }

    public Task<IReadOnlyDictionary<ScriptRef, ScriptDisplayNameEntry>> GetManyAsync(
        string tenantId, IReadOnlyCollection<ScriptRef> refs, CancellationToken ct = default)
    {
        var result = new Dictionary<ScriptRef, ScriptDisplayNameEntry>();
        foreach (var r in refs)
        {
            if (Entries.TryGetValue((tenantId, r.Kind, r.Id), out var entry))
            {
                result[r] = entry;
            }
        }
        return Task.FromResult<IReadOnlyDictionary<ScriptRef, ScriptDisplayNameEntry>>(result);
    }

    public Task UpsertManyAsync(string tenantId, IReadOnlyCollection<ScriptDisplayNameEntry> entries, CancellationToken ct = default)
    {
        foreach (var e in entries)
        {
            Entries[(tenantId, e.Kind, e.ScriptId)] = e;
        }
        return Task.CompletedTask;
    }

    public Task<ScriptNameCacheMeta?> TryGetMetaAsync(string tenantId, ScriptKind kind, CancellationToken ct = default)
    {
        Meta.TryGetValue((tenantId, kind), out var meta);
        return Task.FromResult<ScriptNameCacheMeta?>(meta);
    }

    public Task UpsertMetaAsync(ScriptNameCacheMeta meta, CancellationToken ct = default)
    {
        Meta[(meta.TenantId, meta.Kind)] = meta;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<ScriptDisplayNameEntry> QueryExpiredAsync(
        DateTimeOffset positiveCutoff,
        DateTimeOffset notFoundCutoff,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Snapshot to allow mutation-during-enumeration in tests.
        foreach (var entry in Entries.Values.ToList())
        {
            ct.ThrowIfCancellationRequested();
            var cutoff = entry.IsNotFound ? notFoundCutoff : positiveCutoff;
            if (entry.FetchedAt < cutoff)
            {
                yield return entry;
            }
            await Task.Yield();
        }
    }

    public Task DeleteAsync(string tenantId, ScriptKind kind, string scriptId, CancellationToken ct = default)
    {
        DeleteCalls.Add((tenantId, kind, scriptId));
        if (NextDeleteThrows != null)
        {
            var ex = NextDeleteThrows;
            NextDeleteThrows = null;
            throw ex;
        }
        Entries.Remove((tenantId, kind, scriptId));
        return Task.CompletedTask;
    }
}
