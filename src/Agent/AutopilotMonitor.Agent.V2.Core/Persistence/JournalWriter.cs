#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Serialization;

namespace AutopilotMonitor.Agent.V2.Core.Persistence
{
    /// <summary>
    /// JSONL append-only <see cref="IJournalWriter"/> backed by a single file on disk.
    /// Plan §2.7 / L.12.
    /// </summary>
    public sealed class JournalWriter : IJournalWriter
    {
        private readonly string _path;
        private readonly string _tempPath;
        private readonly Func<DateTime> _utcNow;
        private readonly object _lock = new object();
        private int _lastStepIndex = -1;
        private long _lastTraceOrdinal = -1;

        public JournalWriter(string path)
            : this(path, () => DateTime.UtcNow)
        {
        }

        /// <summary>Overload for deterministic tests — <paramref name="utcNow"/> stamps quarantine buckets
        /// produced by <see cref="TruncateAfter"/>.</summary>
        public JournalWriter(string path, Func<DateTime> utcNow)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("Path is mandatory.", nameof(path));
            }

            _path = path;
            _tempPath = path + ".tmp";
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            ScanRecoveryState();
        }

        public int LastStepIndex
        {
            get
            {
                lock (_lock)
                {
                    return _lastStepIndex;
                }
            }
        }

        public long LastTraceOrdinal
        {
            get
            {
                lock (_lock)
                {
                    return _lastTraceOrdinal;
                }
            }
        }

        public void Append(DecisionTransition transition)
        {
            if (transition == null) throw new ArgumentNullException(nameof(transition));

            lock (_lock)
            {
                if (transition.StepIndex <= _lastStepIndex)
                {
                    throw new InvalidOperationException(
                        $"Journal monotonicity violated: incoming stepIndex {transition.StepIndex} <= lastStepIndex {_lastStepIndex}.");
                }

                var line = TransitionSerializer.Serialize(transition);
                var bytes = Encoding.UTF8.GetBytes(line + "\n");

                using (var fs = new FileStream(
                    _path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    options: FileOptions.WriteThrough))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(flushToDisk: true);
                }

                _lastStepIndex = transition.StepIndex;
                if (transition.SessionTraceOrdinal > _lastTraceOrdinal)
                {
                    _lastTraceOrdinal = transition.SessionTraceOrdinal;
                }
            }
        }

        public IReadOnlyList<DecisionTransition> ReadAll()
        {
            lock (_lock)
            {
                var transitions = new List<DecisionTransition>();
                if (!File.Exists(_path)) return transitions;

                foreach (var rawLine in File.ReadAllLines(_path, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(rawLine)) continue;
                    DecisionTransition parsed;
                    try
                    {
                        parsed = TransitionSerializer.Deserialize(rawLine);
                    }
                    catch
                    {
                        break;
                    }
                    transitions.Add(parsed);
                }

                return transitions;
            }
        }

        public void TruncateAfter(int lastValidStepIndex)
        {
            if (lastValidStepIndex < -1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(lastValidStepIndex),
                    "Boundary must be >= -1 (-1 truncates the entire journal).");
            }

            lock (_lock)
            {
                if (lastValidStepIndex > _lastStepIndex)
                {
                    throw new InvalidOperationException(
                        $"JournalWriter.TruncateAfter: boundary {lastValidStepIndex} exceeds " +
                        $"current LastStepIndex {_lastStepIndex} — can only truncate backwards.");
                }

                if (!File.Exists(_path)) return;
                if (lastValidStepIndex == _lastStepIndex) return; // no-op — already aligned

                // Partition lines into keep-prefix (StepIndex <= boundary) and drop-suffix.
                // Both the parsable transitions and any unparsable tail (the file's own
                // crash-truncation) are conservatively dropped once we cross the boundary.
                var keptLines = new List<string>();
                var droppedLines = new List<string>();
                var boundaryCrossed = false;

                foreach (var rawLine in File.ReadAllLines(_path, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(rawLine))
                    {
                        // Preserve the formatting of the kept prefix; blank lines in the suffix
                        // just follow the first dropped entry and are dropped with it.
                        if (boundaryCrossed) { droppedLines.Add(rawLine); }
                        else { keptLines.Add(rawLine); }
                        continue;
                    }

                    if (boundaryCrossed)
                    {
                        droppedLines.Add(rawLine);
                        continue;
                    }

                    DecisionTransition parsed;
                    try
                    {
                        parsed = TransitionSerializer.Deserialize(rawLine);
                    }
                    catch
                    {
                        // Unparsable line — conservatively treat as suffix: cannot prove the
                        // StepIndex, and keeping it would leave the file inconsistent.
                        boundaryCrossed = true;
                        droppedLines.Add(rawLine);
                        continue;
                    }

                    if (parsed.StepIndex <= lastValidStepIndex)
                    {
                        keptLines.Add(rawLine);
                    }
                    else
                    {
                        boundaryCrossed = true;
                        droppedLines.Add(rawLine);
                    }
                }

                // Forensic trail: persist the dropped suffix before overwriting the journal.
                if (droppedLines.Count > 0)
                {
                    WriteQuarantineFile(droppedLines);
                }

                // Atomic rewrite of the journal — write-temp + flush + replace, same pattern as
                // SnapshotPersistence.Save so a crash mid-truncate leaves the previous file intact.
                var keptPayload = keptLines.Count == 0
                    ? string.Empty
                    : string.Join("\n", keptLines) + "\n";
                var bytes = Encoding.UTF8.GetBytes(keptPayload);

                using (var fs = new FileStream(
                    _tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    options: FileOptions.WriteThrough))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(flushToDisk: true);
                }

                if (File.Exists(_path))
                {
                    File.Replace(_tempPath, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(_tempPath, _path);
                }

                ScanRecoveryState();
            }
        }

        private void WriteQuarantineFile(IReadOnlyList<string> lines)
        {
            var dir = Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(dir)) return; // defensive — ctor guarantees non-empty

            var quarantineRoot = Path.Combine(dir!, ".quarantine");
            if (!Directory.Exists(quarantineRoot))
            {
                Directory.CreateDirectory(quarantineRoot);
            }

            var bucket = Path.Combine(
                quarantineRoot,
                _utcNow().ToString("yyyyMMdd'T'HHmmssfff'Z'"));
            Directory.CreateDirectory(bucket);

            var target = Path.Combine(bucket, "journal-phantom-tail.jsonl");
            File.WriteAllText(target, string.Join("\n", lines) + "\n", Encoding.UTF8);
        }

        /// <summary>
        /// Single-pass scan that populates both <c>_lastStepIndex</c> and
        /// <c>_lastTraceOrdinal</c> from the persisted JSONL. Stops at the first unparsable
        /// line (recovery §2.7).
        /// </summary>
        private void ScanRecoveryState()
        {
            _lastStepIndex = -1;
            _lastTraceOrdinal = -1;

            if (!File.Exists(_path)) return;

            foreach (var line in File.ReadAllLines(_path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var t = TransitionSerializer.Deserialize(line);
                    if (t.StepIndex > _lastStepIndex)
                    {
                        _lastStepIndex = t.StepIndex;
                    }
                    if (t.SessionTraceOrdinal > _lastTraceOrdinal)
                    {
                        _lastTraceOrdinal = t.SessionTraceOrdinal;
                    }
                }
                catch
                {
                    break;
                }
            }
        }
    }
}
