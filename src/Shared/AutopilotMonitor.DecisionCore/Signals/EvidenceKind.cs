namespace AutopilotMonitor.DecisionCore.Signals
{
    /// <summary>
    /// Classification of a signal's evidence origin. Plan §2.2.
    /// </summary>
    public enum EvidenceKind
    {
        /// <summary>Direct observation from a raw source (event log, registry, IME line).</summary>
        Raw,

        /// <summary>Produced by a detector from multiple raw inputs. Requires <c>DerivationInputs</c>.</summary>
        Derived,

        /// <summary>Engine-internal synthesis (deadline fired, classifier verdict, session recovered).</summary>
        Synthetic,
    }
}
