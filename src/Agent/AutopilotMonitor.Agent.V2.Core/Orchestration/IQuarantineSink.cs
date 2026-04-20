#nullable enable
namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Escalation-Hook für den <see cref="DecisionStepProcessor"/> bei wiederholten
    /// Persistenz-Failures. Plan §2.7 Sonderfall 2.
    /// <para>
    /// Wenn das Journal <c>n</c>-mal hintereinander wirft (z.B. Disk-Full, File-Lock),
    /// meldet der Processor das an diese Senke. Der <see cref="EnrollmentOrchestrator"/>
    /// (M4.4.5.b) implementiert das und koordiniert die eigentliche Segment-Quarantäne beim
    /// nächsten Start (M4.4.5.f) — ein laufender Prozess zieht sich die State-Dateien
    /// nicht selbst unter den Füßen weg.
    /// </para>
    /// </summary>
    public interface IQuarantineSink
    {
        /// <summary>
        /// Signalisiert, dass die Session nicht mehr konsistent persistiert werden kann und
        /// beim nächsten Start neu aufgesetzt werden muss. Darf nicht werfen — Ingress-Worker
        /// soll den Alarm absetzen und weiterlaufen.
        /// </summary>
        void TriggerQuarantine(string reason);
    }
}
