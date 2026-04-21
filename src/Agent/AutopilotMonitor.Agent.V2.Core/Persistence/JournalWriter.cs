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
        private readonly object _lock = new object();
        private int _lastStepIndex = -1;
        private long _lastTraceOrdinal = -1;

        public JournalWriter(string path)
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
