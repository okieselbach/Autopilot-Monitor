#nullable enable
using System.Collections.Generic;

namespace AutopilotMonitor.Agent.V2.Core.Transport.Telemetry
{
    /// <summary>
    /// Persistente Queue für <see cref="TelemetryItem"/>s. Plan §2.7a.
    /// <para>
    /// Invarianten:
    /// <list type="bullet">
    ///   <item><b>TelemetryItemId-Vergabe</b>: streng monoton, single-writer — <see cref="Enqueue"/> vergibt
    ///   den Cursor atomar unter Lock.</item>
    ///   <item><b>Sofort-Flush</b> (L.12): jeder erfolgreiche Enqueue steht auf Disk bevor die Methode zurückkehrt.</item>
    ///   <item><b>Idempotenz</b>: <see cref="MarkUploaded"/> mit kleinerer/gleicher ID als der bereits
    ///   persistierte Cursor ist no-op (keine Regression bei Doppel-Aufrufen).</item>
    /// </list>
    /// </para>
    /// </summary>
    public interface ITelemetrySpool
    {
        /// <summary>
        /// Persistiert <paramref name="draft"/>, vergibt <c>TelemetryItemId</c> (und — wenn
        /// <see cref="TelemetryItemDraft.IsSessionScoped"/> — <c>SessionTraceOrdinal</c>), gibt das fertige
        /// <see cref="TelemetryItem"/> zurück.
        /// </summary>
        TelemetryItem Enqueue(TelemetryItemDraft draft);

        /// <summary>
        /// Liest alle noch nicht als uploaded markierten Items in <c>TelemetryItemId</c>-Reihenfolge,
        /// maximal <paramref name="max"/> Stück. Liste kann leer sein.
        /// </summary>
        IReadOnlyList<TelemetryItem> Peek(int max);

        /// <summary>
        /// Markiert alle Items bis einschließlich <paramref name="upToItemIdInclusive"/> als hochgeladen.
        /// Monoton steigend — Aufrufe mit kleinerem Wert sind no-op (Idempotenz-Garantie).
        /// </summary>
        void MarkUploaded(long upToItemIdInclusive);

        /// <summary>Höchste bisher vergebene <c>TelemetryItemId</c>; -1 wenn Spool leer.</summary>
        long LastAssignedItemId { get; }

        /// <summary>Cursor: bis zu dieser ID inklusive wurde bereits erfolgreich hochgeladen; -1 initial.</summary>
        long LastUploadedItemId { get; }
    }
}
