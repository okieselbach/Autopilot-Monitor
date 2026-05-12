using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models.Deletion
{
    /// <summary>
    /// Mutable progress companion to <see cref="DeletionManifest"/>. Tracks which steps have
    /// already executed and whether the live verification pass has run. Plan §3 (Round-2 R9):
    /// schema is intentionally minimal — observability data goes to AuditLogs, not here.
    /// Stored as the sibling <c>{manifestId}.progress.json</c> blob and CAS'd on every write.
    /// </summary>
    public class DeletionProgress
    {
        /// <summary>SHA-256 of the immutable snapshot blob; mismatch = corruption, refuse to proceed.</summary>
        public string SnapshotSha256 { get; set; } = string.Empty;

        /// <summary>Step.Order values that have completed; the worker iterates the remaining ones.</summary>
        public HashSet<int> CompletedSteps { get; set; } = new HashSet<int>();

        /// <summary>True once the live verification pass succeeded; gates the FINAL tombstone step.</summary>
        public bool VerificationDone { get; set; }

        /// <summary>UTC timestamp once the FINAL tombstone step has completed; null while in flight.</summary>
        public DateTime? CompletedAt { get; set; }

        // ============================================================ PR4c additions ====
        // Three additive fields close the per-key idempotency + tombstone-gap correctness holes
        // discovered by Codex review of PR4 + PR4b. All three default to safe values
        // (null / false) so PR1–PR4 progress blobs without these fields deserialize cleanly.

        /// <summary>
        /// PR4c F1: Composite keys (<c>{Vendor}:{Name}:{Version}</c>) of SoftwareInventory
        /// decrements already applied by the cascade worker's AGGREGATE step. Per-key
        /// persistence so a crash mid-decrement-loop doesn't double-decrement on retry.
        /// Worker writes one entry after each successful <c>DecrementSoftwareInventoryEntryAsync</c>
        /// and persists the progress blob with ETag-CAS, then moves to the next key.
        /// Null on PR1-PR4 progress blobs (worker initializes on first AGGREGATE-step entry).
        /// </summary>
        public HashSet<string>? AggregateDecrementsApplied { get; set; }

        /// <summary>
        /// PR4c F4: Composite keys of SoftwareInventory re-increments already applied by the
        /// partial-restore service. Per-key persistence so a crash between the re-increment
        /// loop and the final <c>Poisoned → None</c> CAS doesn't double-increment counters on
        /// retry. Service writes one entry after each successful
        /// <c>RestoreSoftwareInventoryEntryByKeyAsync</c> and persists with ETag-CAS.
        /// Null on PR1-PR4 progress blobs (service initializes on first re-increment).
        /// </summary>
        public HashSet<string>? RestoreReIncrementsApplied { get; set; }

        /// <summary>
        /// PR4c F2: Set to <c>true</c> by the cascade worker <b>before</b> issuing the first
        /// FINAL-step row delete. Closes the "tombstone gap": if the worker dies between
        /// deleting the Sessions row and writing <see cref="CompletedAt"/>, restore can still
        /// dispatch into full-restore mode by reading this flag (otherwise
        /// <c>sessions=null + completedAt=null</c> looks like corruption and restore rejects).
        /// Default <c>false</c> for back-compat — PR1-PR4 progress blobs without this field
        /// deserialize cleanly to <c>false</c>, preserving the existing "corrupt-state" reject
        /// behaviour for genuine bugs (Sessions row removed outside the cascade).
        /// </summary>
        public bool TombstoneStarted { get; set; }
    }
}
