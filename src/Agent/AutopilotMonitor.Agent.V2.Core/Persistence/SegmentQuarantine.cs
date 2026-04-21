#nullable enable
using System;
using System.IO;
using System.Text;

namespace AutopilotMonitor.Agent.V2.Core.Persistence
{
    /// <summary>
    /// Verschiebt beschädigte Persistenz-Segmente (SignalLog, Journal, EventSequence) in einen
    /// timestamped <c>.quarantine/{ts}/</c>-Bucket. Plan §2.7 Sonderfall 2 / §4.x M4.4.5.f.
    /// <para>
    /// Parallel zu <see cref="SnapshotPersistence.Quarantine"/> — der Caller
    /// (<see cref="Orchestration.EnrollmentOrchestrator"/>) bewegt den Snapshot via
    /// <c>SnapshotPersistence.Quarantine</c> und die Log-Segmente via diesem Helper.
    /// Beide Aufrufe schreiben in <c>&lt;stateDir&gt;/.quarantine/&lt;timestamp&gt;/</c>.
    /// </para>
    /// <para>
    /// <b>Invariante</b>: wenn die Methode erfolgreich zurückkehrt, sind die bekannten
    /// Log-Files aus <paramref name="stateDirectory"/> verschwunden — der nächste Append
    /// legt frische Dateien an, der Reducer startet im Initial-State.
    /// </para>
    /// </summary>
    public static class SegmentQuarantine
    {
        /// <summary>Die bekannten Segment-Dateien im State-Verzeichnis (neben snapshot.json).</summary>
        private static readonly string[] KnownSegmentFiles = new[]
        {
            "signal-log.jsonl",
            "journal.jsonl",
            "event-sequence.json",
        };

        /// <summary>
        /// Bewegt alle existierenden Segment-Files in einen timestamped Bucket. Kein-Op
        /// wenn das State-Verzeichnis nicht existiert.
        /// </summary>
        public static void QuarantineAll(string stateDirectory, string reason, Func<DateTime> utcNow)
        {
            if (string.IsNullOrEmpty(stateDirectory)) throw new ArgumentException("stateDirectory is mandatory.", nameof(stateDirectory));
            if (utcNow == null) throw new ArgumentNullException(nameof(utcNow));
            if (!Directory.Exists(stateDirectory)) return;

            var quarantineRoot = Path.Combine(stateDirectory, ".quarantine");
            if (!Directory.Exists(quarantineRoot))
            {
                Directory.CreateDirectory(quarantineRoot);
            }

            var bucket = Path.Combine(
                quarantineRoot,
                utcNow().ToString("yyyyMMdd'T'HHmmssfff'Z'"));
            Directory.CreateDirectory(bucket);

            var movedAny = false;
            foreach (var name in KnownSegmentFiles)
            {
                var src = Path.Combine(stateDirectory, name);
                if (File.Exists(src))
                {
                    File.Move(src, Path.Combine(bucket, name));
                    movedAny = true;
                }
            }

            // Reason-Sidecar immer schreiben, auch wenn keine Datei bewegt wurde — macht die
            // Quarantäne-Historie nachvollziehbar.
            File.WriteAllText(Path.Combine(bucket, "reason.txt"), reason ?? string.Empty, Encoding.UTF8);

            // Wenn nichts zu bewegen war → leeren Bucket entfernen, damit kein leeres
            // Quarantine-Verzeichnis bleibt.
            if (!movedAny)
            {
                try { File.Delete(Path.Combine(bucket, "reason.txt")); } catch { /* best-effort */ }
                try { Directory.Delete(bucket); } catch { /* best-effort */ }
            }
        }
    }
}
