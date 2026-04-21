#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AutopilotMonitor.DecisionCore.Serialization;
using AutopilotMonitor.DecisionCore.Signals;

namespace AutopilotMonitor.Agent.V2.Core.Persistence
{
    /// <summary>
    /// JSONL append-only <see cref="ISignalLogWriter"/> backed by a single file on disk.
    /// Plan §2.7 / L.12 (Sofort-Flush).
    /// </summary>
    public sealed class SignalLogWriter : ISignalLogWriter
    {
        private readonly string _path;
        private readonly object _lock = new object();
        private long _lastOrdinal = -1;
        private long _lastTraceOrdinal = -1;

        public SignalLogWriter(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("Path is mandatory.", nameof(path));
            }

            _path = path;

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            ScanRecoveryState();
        }

        public long LastOrdinal
        {
            get
            {
                lock (_lock)
                {
                    return _lastOrdinal;
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

        public void Append(DecisionSignal signal)
        {
            if (signal == null) throw new ArgumentNullException(nameof(signal));

            lock (_lock)
            {
                if (signal.SessionSignalOrdinal <= _lastOrdinal)
                {
                    throw new InvalidOperationException(
                        $"SignalLog monotonicity violated: incoming ordinal {signal.SessionSignalOrdinal} <= lastOrdinal {_lastOrdinal}.");
                }

                var line = SignalSerializer.Serialize(signal);
                var bytes = Encoding.UTF8.GetBytes(line + "\n");

                // WriteThrough + Flush(true) — belt-and-suspenders per L.12: OS cache
                // bypass plus explicit disk flush. Any return from Append means on-disk.
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

                _lastOrdinal = signal.SessionSignalOrdinal;
                if (signal.SessionTraceOrdinal > _lastTraceOrdinal)
                {
                    _lastTraceOrdinal = signal.SessionTraceOrdinal;
                }
            }
        }

        public IReadOnlyList<DecisionSignal> ReadAll()
        {
            lock (_lock)
            {
                var signals = new List<DecisionSignal>();
                if (!File.Exists(_path))
                {
                    return signals;
                }

                foreach (var rawLine in File.ReadAllLines(_path, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(rawLine)) continue;

                    DecisionSignal parsed;
                    try
                    {
                        parsed = SignalSerializer.Deserialize(rawLine);
                    }
                    catch
                    {
                        // Recovery rule §2.7: corrupt tail (crash mid-append) → stop at last
                        // parsable line. Ordinals are monotonic, so everything before this is
                        // still valid. Logging is the Orchestrator's concern in M4.4.
                        break;
                    }

                    signals.Add(parsed);
                }

                return signals;
            }
        }

        /// <summary>
        /// Single-pass scan that populates both <c>_lastOrdinal</c> and <c>_lastTraceOrdinal</c>
        /// from the persisted JSONL. Stops at the first unparsable line (recovery §2.7).
        /// </summary>
        private void ScanRecoveryState()
        {
            _lastOrdinal = -1;
            _lastTraceOrdinal = -1;

            if (!File.Exists(_path)) return;

            foreach (var line in File.ReadAllLines(_path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var sig = SignalSerializer.Deserialize(line);
                    if (sig.SessionSignalOrdinal > _lastOrdinal)
                    {
                        _lastOrdinal = sig.SessionSignalOrdinal;
                    }
                    if (sig.SessionTraceOrdinal > _lastTraceOrdinal)
                    {
                        _lastTraceOrdinal = sig.SessionTraceOrdinal;
                    }
                }
                catch
                {
                    // Stop at first unparsable line — recovery §2.7.
                    break;
                }
            }
        }
    }
}
