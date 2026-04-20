using System;
using System.Collections.Generic;

namespace AutopilotMonitor.DecisionCore.Signals
{
    /// <summary>
    /// Immutable evidence record attached to every <see cref="DecisionSignal"/>.
    /// Plan §2.2 — pflichtfeld-validation in the signal adapter; signal without
    /// evidence = bug. Per-kind mandatory fields:
    /// <list type="bullet">
    ///   <item>Raw: <see cref="Kind"/>, <see cref="Identifier"/>, <see cref="Summary"/></item>
    ///   <item>Derived: all Raw fields + non-empty <see cref="DerivationInputs"/></item>
    ///   <item>Synthetic: <see cref="Kind"/>, <see cref="Identifier"/>, <see cref="Summary"/></item>
    /// </list>
    /// </summary>
    public sealed class Evidence
    {
        public Evidence(
            EvidenceKind kind,
            string identifier,
            string summary,
            string? rawPointer = null,
            IReadOnlyDictionary<string, string>? derivationInputs = null)
        {
            if (string.IsNullOrEmpty(identifier))
            {
                throw new ArgumentException("Evidence.Identifier is mandatory.", nameof(identifier));
            }

            if (summary == null)
            {
                throw new ArgumentNullException(nameof(summary));
            }

            if (kind == EvidenceKind.Derived && (derivationInputs == null || derivationInputs.Count == 0))
            {
                throw new ArgumentException(
                    "Evidence.Derived requires at least one DerivationInputs entry.",
                    nameof(derivationInputs));
            }

            Kind = kind;
            Identifier = identifier;
            Summary = summary.Length > 256 ? summary.Substring(0, 256) : summary;
            RawPointer = rawPointer;
            DerivationInputs = derivationInputs;
        }

        public EvidenceKind Kind { get; }

        /// <summary>
        /// Raw: RecordId / RegistryPath / LogLineHash.
        /// Derived: detectorId + version (e.g. "desktop-arrival-detector-v1").
        /// Synthetic: correlationId (e.g. "deadline:hello_safety:fired").
        /// </summary>
        public string Identifier { get; }

        /// <summary>UI-friendly summary, ≤256 chars (truncated on construction).</summary>
        public string Summary { get; }

        /// <summary>Optional blob key for full raw payload retrieval.</summary>
        public string? RawPointer { get; }

        /// <summary>Derived-only: which raw observations fed the detector.</summary>
        public IReadOnlyDictionary<string, string>? DerivationInputs { get; }
    }
}
