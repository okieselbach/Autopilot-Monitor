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

            _lastStepIndex = ScanHighestStepIndex();
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

        private int ScanHighestStepIndex()
        {
            if (!File.Exists(_path)) return -1;

            int highest = -1;
            foreach (var line in File.ReadAllLines(_path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var t = TransitionSerializer.Deserialize(line);
                    if (t.StepIndex > highest)
                    {
                        highest = t.StepIndex;
                    }
                }
                catch
                {
                    break;
                }
            }
            return highest;
        }
    }
}
