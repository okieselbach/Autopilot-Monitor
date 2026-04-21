#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AutopilotMonitor.Agent.V2.Core.Transport.Telemetry
{
    /// <summary>
    /// Einziger Transport-Einstiegspunkt im V2-Agent. Plan §2.7a / L.10.
    /// <para>
    /// Vereint Enqueue (mit <c>TelemetryItemId</c>-Vergabe) und Drain (koordiniert + idempotent + resume-fähig).
    /// </para>
    /// </summary>
    public interface ITelemetryTransport : IDisposable
    {
        /// <summary>Persistiert <paramref name="draft"/> im Spool und gibt das fertige <see cref="TelemetryItem"/> zurück.</summary>
        TelemetryItem Enqueue(TelemetryItemDraft draft);

        /// <summary>
        /// Drainiert alle bisher enqueued Items in <c>TelemetryItemId</c>-Reihenfolge gegen das Backend.
        /// Idempotent, resume-fähig. Wirft nicht bei transienten Fehlern — gibt <see cref="DrainResult"/> zurück.
        /// </summary>
        Task<DrainResult> DrainAllAsync(CancellationToken cancellationToken = default);

        /// <summary>Cursor für Crash-Recovery (§2.7 recovery-step 3); <c>-1</c> wenn noch nichts hochgeladen.</summary>
        long LastUploadedItemId { get; }
    }
}
