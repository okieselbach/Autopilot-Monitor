namespace AutopilotMonitor.DecisionCore.Classifiers
{
    /// <summary>
    /// A classifier-input snapshot that exposes a deterministic hash. Plan §2.4 anti-loop.
    /// <para>
    /// Implementations must produce the same hash byte-for-byte for equivalent snapshots,
    /// across process restarts, regardless of hashtable ordering or culture. The hash is
    /// used by the effect runner / replay harness to skip classifier invocations whose
    /// input matches the last verdict's <see cref="ClassifierVerdict.InputHash"/>.
    /// </para>
    /// </summary>
    public interface IClassifierSnapshot
    {
        string ComputeInputHash();
    }
}
