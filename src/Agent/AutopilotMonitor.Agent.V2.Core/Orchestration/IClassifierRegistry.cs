#nullable enable
using AutopilotMonitor.DecisionCore.Classifiers;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Lookup-by-id für registrierte <see cref="IClassifier"/>-Instanzen. Plan §2.4.
    /// <para>
    /// Der EffectRunner konsultiert die Registry bei <c>RunClassifier</c>-Effekten.
    /// Registry ist read-only nach Konstruktion — Classifiers werden beim Orchestrator-Start
    /// einmalig registriert (§2.4 „Kernel-Service").
    /// </para>
    /// </summary>
    public interface IClassifierRegistry
    {
        bool TryGet(string classifierId, out IClassifier? classifier);
    }
}
