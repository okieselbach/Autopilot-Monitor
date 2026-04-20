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

            _lastOrdinal = ScanHighestOrdinal();
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

        private long ScanHighestOrdinal()
        {
            if (!File.Exists(_path)) return -1;

            long highest = -1;
            foreach (var line in File.ReadAllLines(_path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var sig = SignalSerializer.Deserialize(line);
                    if (sig.SessionSignalOrdinal > highest)
                    {
                        highest = sig.SessionSignalOrdinal;
                    }
                }
                catch
                {
                    // Stop at first unparsable line — recovery §2.7.
                    break;
                }
            }
            return highest;
        }
    }
}
