using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Diagnostics;
using AutopilotMonitor.Shared.Models.Deletion;

namespace AutopilotMonitor.Functions.Tests.Helpers;

/// <summary>
/// No-op implementation of <see cref="DiagnosticsBlobCascadeDeleter"/> for existing
/// SessionDeletionHandler test harnesses that pre-date the §5b cascade-delete step
/// for the diagnostics blob. Subclasses the production type via the protected
/// test-seam ctor and short-circuits <see cref="DeleteAsync"/> to a successful
/// no-op so the cascade flow under test reaches the tombstone step unchanged.
/// Tests that specifically exercise the §5b branching use Mock&lt;...&gt; directly.
/// </summary>
public sealed class NoOpDiagnosticsBlobCascadeDeleter : DiagnosticsBlobCascadeDeleter
{
    public int CallCount { get; private set; }

    public override Task<DiagnosticsBlobDeleteOutcome> DeleteAsync(
        DeletionManifest manifest, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(DiagnosticsBlobDeleteOutcome.SkippedNoBlob);
    }
}
