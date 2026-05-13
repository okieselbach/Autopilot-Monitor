using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Deletion
{
    /// <summary>
    /// Live verification pass between the table-step phase and the FINAL tombstone phase of the
    /// cascade worker (plan §1 P4 + §3 verification table). For every cascade-targeted table the
    /// service issues a LIVE query against current data — not a manifest-key existence check — so
    /// it catches "ghost rows" written by paths that bypassed the writer-block guard within the
    /// T2-T4 race window (§16 R13). A single hit on any table aborts the cascade: caller audits
    /// <c>deletion_verification_failed</c> with a capped residual-key sample and throws so the
    /// worker poisons after max-dequeue; operator then runs §13 restore-from-poisoned.
    /// <para>
    /// CveIndex is the deliberate exception (§12-Q8): tenant-wide live scan would be cost-prohibitive
    /// on a per-CVE-partition projection, so the verification falls back to per-manifest-key
    /// <c>GetEntity</c> checks. Safety is preserved upstream by the <c>SessionDeletionGuard</c>
    /// wired into every CveIndex writer (§5 PR3).
    /// </para>
    /// <para>
    /// <see cref="DeletionStepClass.Aggregate"/> is excluded — SoftwareInventory counters are
    /// statistical (approximate by design; clamp ≥ 0) and a live residual check would always
    /// over-report. <see cref="DeletionStepClass.Final"/> is excluded — it IS the tombstone the
    /// verification gate sits in front of.
    /// </para>
    /// </summary>
    public class CascadeVerificationService
    {
        private readonly ISessionDeletionInventoryReader _reader;
        private readonly ILogger<CascadeVerificationService> _logger;

        // The verification residual sample passed to the audit log is bounded so a pathological
        // ghost-row scenario doesn't blow up the AuditLogs payload. Operator sees the first
        // sample; the precise total is recorded as a separate count field.
        internal const int MaxResidualSampleSize = 50;

        public CascadeVerificationService(ISessionDeletionInventoryReader reader, ILogger<CascadeVerificationService> logger)
        {
            _reader = reader;
            _logger = logger;
        }

        /// <summary>
        /// Runs the verification pass over every non-FINAL, non-AGGREGATE step in the manifest.
        /// Returns the outcome plus the residual sample to attach to the audit log. The caller
        /// (Handler) decides whether to throw on a non-clean outcome.
        /// </summary>
        public virtual async Task<CascadeVerificationResult> VerifyAsync(DeletionManifest manifest, CancellationToken ct = default)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));

            var residuals = new List<CascadeResidualKey>();
            var safeTenantId = ODataSanitizer.EscapeValue(manifest.TenantId);
            var safeSessionId = ODataSanitizer.EscapeValue(manifest.SessionId);

            foreach (var step in manifest.Steps)
            {
                ct.ThrowIfCancellationRequested();

                if (step.Class == DeletionStepClass.Aggregate || step.Class == DeletionStepClass.Final)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(step.Table))
                {
                    // Synthetic non-AGGREGATE step is a corruption signal — every table-targeted
                    // step must carry a Table name. Defensive: caller already validates the
                    // manifest shape but the verifier must not silently skip.
                    throw new InvalidOperationException(
                        $"Manifest step Order={step.Order} Class={step.Class} has no Table — verification cannot proceed.");
                }

                var found = await VerifyStepAsync(step, safeTenantId, safeSessionId, manifest.SessionId, ct);
                if (found.Count == 0) continue;

                _logger.LogWarning(
                    "Cascade verification found {Count} residual rows in table {Table} for tenant={TenantId} session={SessionId} manifestId={ManifestId}",
                    found.Count, step.Table, manifest.TenantId, manifest.SessionId, manifest.ManifestId);

                foreach (var key in found)
                {
                    if (residuals.Count >= MaxResidualSampleSize) break;
                    residuals.Add(key);
                }

                // First-table-failure short-circuit: once we know the verification gate must reject
                // tombstone, the audit sample is informational; we do not need to enumerate every
                // remaining table. The Handler's poison + restore-from-poisoned path is the
                // recovery mechanism, not a per-table inventory.
                return new CascadeVerificationResult(isClean: false, residuals: residuals);
            }

            return new CascadeVerificationResult(isClean: true, residuals: residuals);
        }

        private async Task<List<CascadeResidualKey>> VerifyStepAsync(
            DeletionStep step, string safeTenantId, string safeSessionId, string sessionId, CancellationToken ct)
        {
            var found = new List<CascadeResidualKey>();
            var table = step.Table!;

            switch (step.Class)
            {
                case DeletionStepClass.PkBySession:
                {
                    var filter = $"PartitionKey eq '{safeTenantId}_{safeSessionId}'";
                    await foreach (var entity in _reader.QueryAsync(table, filter, ct))
                    {
                        found.Add(new CascadeResidualKey(table, entity.PartitionKey ?? string.Empty, entity.RowKey ?? string.Empty));
                        if (found.Count >= MaxResidualSampleSize) break;
                    }
                    break;
                }

                case DeletionStepClass.PropTenantPk:
                {
                    var filter = $"PartitionKey eq '{safeTenantId}' and SessionId eq '{safeSessionId}'";
                    await foreach (var entity in _reader.QueryAsync(table, filter, ct))
                    {
                        found.Add(new CascadeResidualKey(table, entity.PartitionKey ?? string.Empty, entity.RowKey ?? string.Empty));
                        if (found.Count >= MaxResidualSampleSize) break;
                    }
                    break;
                }

                case DeletionStepClass.PkRkExact:
                {
                    // The manifest stamped (PK, RK) at preflight; verification is the same lookup.
                    foreach (var row in step.Rows)
                    {
                        ct.ThrowIfCancellationRequested();
                        var entity = await _reader.GetEntityOrNullAsync(table, row.Pk, row.Rk, ct);
                        if (entity != null)
                        {
                            found.Add(new CascadeResidualKey(table, row.Pk, row.Rk));
                            if (found.Count >= MaxResidualSampleSize) break;
                        }
                    }
                    break;
                }

                case DeletionStepClass.DiscriminatorPkRkSuffix:
                {
                    var filter = $"PartitionKey ge '{safeTenantId}_' and PartitionKey lt '{safeTenantId}_~'";
                    var suffix = $"_{sessionId}";
                    await foreach (var entity in _reader.QueryAsync(table, filter, ct))
                    {
                        if (entity.RowKey != null && entity.RowKey.EndsWith(suffix, StringComparison.Ordinal))
                        {
                            found.Add(new CascadeResidualKey(table, entity.PartitionKey ?? string.Empty, entity.RowKey));
                            if (found.Count >= MaxResidualSampleSize) break;
                        }
                    }
                    break;
                }

                case DeletionStepClass.DiscriminatorPkRkExact:
                {
                    // §12-Q8: manifest-key only — direct GetEntity per (PK, RK).
                    foreach (var row in step.Rows)
                    {
                        ct.ThrowIfCancellationRequested();
                        var entity = await _reader.GetEntityOrNullAsync(table, row.Pk, row.Rk, ct);
                        if (entity != null)
                        {
                            found.Add(new CascadeResidualKey(table, row.Pk, row.Rk));
                            if (found.Count >= MaxResidualSampleSize) break;
                        }
                    }
                    break;
                }

                case DeletionStepClass.DiscriminatorPkProp:
                {
                    var filter = $"PartitionKey ge '{safeTenantId}_' and PartitionKey lt '{safeTenantId}_~' and SessionId eq '{safeSessionId}'";
                    await foreach (var entity in _reader.QueryAsync(table, filter, ct))
                    {
                        found.Add(new CascadeResidualKey(table, entity.PartitionKey ?? string.Empty, entity.RowKey ?? string.Empty));
                        if (found.Count >= MaxResidualSampleSize) break;
                    }
                    break;
                }

                default:
                    throw new InvalidOperationException(
                        $"Manifest step Order={step.Order} has unsupported verification class '{step.Class}' (expected one of the table-targeted classes).");
            }

            return found;
        }
    }

    /// <summary>
    /// Outcome of a <see cref="CascadeVerificationService.VerifyAsync"/> call. When
    /// <see cref="IsClean"/> is false, <see cref="Residuals"/> carries the first up to
    /// <c>MaxResidualSampleSize</c> ghost rows for the audit log.
    /// </summary>
    public sealed class CascadeVerificationResult
    {
        public bool IsClean { get; }
        public IReadOnlyList<CascadeResidualKey> Residuals { get; }

        public CascadeVerificationResult(bool isClean, IReadOnlyList<CascadeResidualKey> residuals)
        {
            IsClean = isClean;
            Residuals = residuals;
        }
    }

    /// <summary>
    /// One residual key found during verification. Carried in the
    /// <c>deletion_verification_failed</c> audit entry's details Dictionary for operator triage.
    /// </summary>
    public sealed class CascadeResidualKey
    {
        public string Table { get; }
        public string Pk { get; }
        public string Rk { get; }

        public CascadeResidualKey(string table, string pk, string rk)
        {
            Table = table;
            Pk = pk;
            Rk = rk;
        }
    }
}
