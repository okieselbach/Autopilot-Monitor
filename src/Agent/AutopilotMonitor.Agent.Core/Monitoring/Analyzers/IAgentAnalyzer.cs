namespace AutopilotMonitor.Agent.Core.Monitoring.Analyzers
{
    /// <summary>
    /// Represents an agent-side security or configuration analyzer.
    /// Unlike collectors which stream raw telemetry, analyzers run targeted checks,
    /// produce a confidence-scored finding, and emit a single structured event.
    ///
    /// Lifecycle:
    ///   AnalyzeAtStartup()  — called once after agent starts, before enrollment phases begin
    ///   AnalyzeAtShutdown() — called once at enrollment end / agent stop (enables delta detection)
    /// </summary>
    public interface IAgentAnalyzer
    {
        /// <summary>
        /// Human-readable name for logging (e.g., "LocalAdminAnalyzer").
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Run analysis at agent startup.
        /// Emits a structured finding event via the callback supplied at construction.
        /// </summary>
        void AnalyzeAtStartup();

        /// <summary>
        /// Run analysis at agent shutdown / enrollment completion (delta detection).
        /// Emits a second finding event for end-state comparison against the startup result.
        /// IMPORTANT: Emitted events MUST use Phase=Unknown. Analyzers are not phase-declaration
        /// events — see EnrollmentPhase doc for the phase strategy.
        /// </summary>
        void AnalyzeAtShutdown();
    }
}
