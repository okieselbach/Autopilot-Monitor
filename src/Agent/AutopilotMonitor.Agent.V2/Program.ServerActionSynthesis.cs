#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Orchestration;

namespace AutopilotMonitor.Agent.V2
{
    /// <summary>
    /// Helpers for synthesising Terminated-event args out of <c>ServerAction</c> payloads
    /// (Plan §4.x M4.6.ε, Codex Finding 2 fix). Pure + internal so
    /// <c>AutopilotMonitor.Agent.V2.Core.Tests</c> can exercise them against canned param
    /// dictionaries without a full orchestrator harness.
    /// </summary>
    public static partial class Program
    {
        /// <summary>
        /// Maps a <c>terminate_session</c> ServerAction's <c>adminOutcome</c> param onto an
        /// <see cref="EnrollmentTerminationOutcome"/>. Portal Mark-Succeeded was previously
        /// hard-coded to <see cref="EnrollmentTerminationOutcome.Failed"/>, so operators saw
        /// failures in SummaryDialog + got diagnostics uploads fired even when they had
        /// manually marked a session succeeded.
        /// <para>
        /// Mapping:
        /// <list type="bullet">
        ///   <item><c>"Succeeded"</c> (case-insensitive) → <see cref="EnrollmentTerminationOutcome.Succeeded"/>.</item>
        ///   <item><c>"Failed"</c>, other non-empty values → <see cref="EnrollmentTerminationOutcome.Failed"/>.</item>
        ///   <item>Missing/null/empty → <see cref="EnrollmentTerminationOutcome.Failed"/> (default-failure-safe
        ///     for a <c>terminate_session</c> action with no explicit outcome — e.g. a kill-signal-driven
        ///     synthesis that only sets <c>origin=kill_signal</c>).</item>
        /// </list>
        /// </para>
        /// </summary>
        internal static EnrollmentTerminationOutcome MapAdminOutcome(IReadOnlyDictionary<string, string>? parameters)
        {
            if (parameters == null) return EnrollmentTerminationOutcome.Failed;
            if (!parameters.TryGetValue("adminOutcome", out var value) || string.IsNullOrEmpty(value))
                return EnrollmentTerminationOutcome.Failed;

            return string.Equals(value, "Succeeded", StringComparison.OrdinalIgnoreCase)
                ? EnrollmentTerminationOutcome.Succeeded
                : EnrollmentTerminationOutcome.Failed;
        }
    }
}
