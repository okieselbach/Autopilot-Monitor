#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Classifiers;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Default dictionary-backed <see cref="IClassifierRegistry"/>. Plan §2.4.
    /// </summary>
    public sealed class ClassifierRegistry : IClassifierRegistry
    {
        private readonly Dictionary<string, IClassifier> _byId;

        public ClassifierRegistry(IEnumerable<IClassifier> classifiers)
        {
            if (classifiers == null) throw new ArgumentNullException(nameof(classifiers));

            _byId = new Dictionary<string, IClassifier>(StringComparer.Ordinal);
            foreach (var c in classifiers)
            {
                if (c == null) throw new ArgumentException("Classifier enumerable must not contain null.", nameof(classifiers));
                if (string.IsNullOrEmpty(c.Id))
                {
                    throw new ArgumentException($"Classifier {c.GetType().FullName} has empty Id.", nameof(classifiers));
                }
                if (_byId.ContainsKey(c.Id))
                {
                    throw new ArgumentException($"Duplicate classifier id: '{c.Id}'.", nameof(classifiers));
                }
                _byId[c.Id] = c;
            }
        }

        public bool TryGet(string classifierId, out IClassifier? classifier)
        {
            if (string.IsNullOrEmpty(classifierId))
            {
                classifier = null;
                return false;
            }

            if (_byId.TryGetValue(classifierId, out var found))
            {
                classifier = found;
                return true;
            }

            classifier = null;
            return false;
        }
    }
}
