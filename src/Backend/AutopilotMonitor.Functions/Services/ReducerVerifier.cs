using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Structural verification of a session's persisted SignalLog + DecisionTransitions
    /// journal. Pure function — takes loaded records, produces a report. No IO, no DI.
    /// See <see cref="ReducerVerificationReport"/> for scope boundary vs. full engine replay.
    /// </summary>
    internal static class ReducerVerifier
    {
        public static ReducerVerificationReport Verify(
            string tenantId,
            string sessionId,
            IReadOnlyList<SignalRecord> signals,
            IReadOnlyList<DecisionTransitionRecord> transitions,
            string currentReducerVersion)
        {
            var report = new ReducerVerificationReport
            {
                TenantId              = tenantId,
                SessionId             = sessionId,
                SignalCount           = signals?.Count ?? 0,
                TransitionCount       = transitions?.Count ?? 0,
                CurrentReducerVersion = currentReducerVersion ?? string.Empty,
                Issues                = new List<VerificationIssue>(),
            };

            if ((signals == null || signals.Count == 0) && (transitions == null || transitions.Count == 0))
            {
                report.Issues.Add(new VerificationIssue
                {
                    Severity = "Info",
                    Kind     = "empty_session",
                    Message  = "Session has no signals or transitions persisted.",
                });
                return report;
            }

            // ---- Signal ordinal contiguity ---------------------------------------------
            if (signals != null && signals.Count > 0)
            {
                var ordered = signals.OrderBy(s => s.SessionSignalOrdinal).ToList();
                report.SignalOrdinalFirst = ordered[0].SessionSignalOrdinal;
                report.SignalOrdinalLast  = ordered[ordered.Count - 1].SessionSignalOrdinal;

                var gaps = new List<(long Prev, long Next)>();
                for (var i = 1; i < ordered.Count; i++)
                {
                    var prev = ordered[i - 1].SessionSignalOrdinal;
                    var next = ordered[i].SessionSignalOrdinal;
                    if (next != prev + 1) gaps.Add((prev, next));
                }
                report.SignalOrdinalsContiguous = gaps.Count == 0;

                // Report gaps individually up to a cap — an attacker could fill the report
                // with thousands of issues otherwise.
                const int maxGapIssues = 20;
                foreach (var (prev, next) in gaps.Take(maxGapIssues))
                {
                    report.Issues.Add(new VerificationIssue
                    {
                        Severity = "Warning",
                        Kind     = "signal_ordinal_gap",
                        Message  = $"Signal ordinal jumps from {prev} to {next} (missing {next - prev - 1} ordinal(s)).",
                    });
                }
                if (gaps.Count > maxGapIssues)
                {
                    report.Issues.Add(new VerificationIssue
                    {
                        Severity = "Warning",
                        Kind     = "signal_ordinal_gap",
                        Message  = $"... {gaps.Count - maxGapIssues} additional ordinal gaps not listed.",
                    });
                }
            }

            // ---- Transition step-index contiguity --------------------------------------
            if (transitions != null && transitions.Count > 0)
            {
                var ordered = transitions.OrderBy(t => t.StepIndex).ToList();
                report.StepIndexFirst = ordered[0].StepIndex;
                report.StepIndexLast  = ordered[ordered.Count - 1].StepIndex;

                var gaps = new List<(int Prev, int Next)>();
                for (var i = 1; i < ordered.Count; i++)
                {
                    var prev = ordered[i - 1].StepIndex;
                    var next = ordered[i].StepIndex;
                    if (next != prev + 1) gaps.Add((prev, next));
                }
                report.StepIndicesContiguous = gaps.Count == 0;

                const int maxGapIssues = 20;
                foreach (var (prev, next) in gaps.Take(maxGapIssues))
                {
                    report.Issues.Add(new VerificationIssue
                    {
                        Severity = "Warning",
                        Kind     = "step_index_gap",
                        Message  = $"Transition StepIndex jumps from {prev} to {next} (missing {next - prev - 1} step(s)).",
                    });
                }
                if (gaps.Count > maxGapIssues)
                {
                    report.Issues.Add(new VerificationIssue
                    {
                        Severity = "Warning",
                        Kind     = "step_index_gap",
                        Message  = $"... {gaps.Count - maxGapIssues} additional step-index gaps not listed.",
                    });
                }

                // StoredReducerVersion + drift check (first transition is representative; the
                // agent-side Journal contract guarantees per-session consistency).
                report.StoredReducerVersion = ordered[0].ReducerVersion;
                report.ReducerVersionDrift = !string.IsNullOrEmpty(report.CurrentReducerVersion) &&
                                             !string.IsNullOrEmpty(report.StoredReducerVersion) &&
                                             !string.Equals(report.StoredReducerVersion, report.CurrentReducerVersion, StringComparison.Ordinal);
                if (report.ReducerVersionDrift)
                {
                    report.Issues.Add(new VerificationIssue
                    {
                        Severity = "Info",
                        Kind     = "reducer_version_drift",
                        Message  = $"Stored ReducerVersion {report.StoredReducerVersion} differs from current {report.CurrentReducerVersion}. " +
                                   "Session was journaled under an older reducer build — structural checks still valid, but " +
                                   "a replay would not reproduce the stored transitions exactly.",
                    });
                }

                // ---- Cross-reference: SignalOrdinalRef → existing signal --------------
                if (signals != null && signals.Count > 0)
                {
                    var signalOrdinals = new HashSet<long>(signals.Select(s => s.SessionSignalOrdinal));
                    var orphanCount = 0;
                    const int maxOrphanIssues = 20;

                    foreach (var t in ordered)
                    {
                        if (!signalOrdinals.Contains(t.SignalOrdinalRef))
                        {
                            orphanCount++;
                            if (orphanCount <= maxOrphanIssues)
                            {
                                report.Issues.Add(new VerificationIssue
                                {
                                    Severity = "Warning",
                                    Kind     = "orphaned_transition",
                                    Message  = $"Transition StepIndex={t.StepIndex} references SignalOrdinal={t.SignalOrdinalRef} which is not in the loaded signal set.",
                                });
                            }
                        }
                    }
                    if (orphanCount > maxOrphanIssues)
                    {
                        report.Issues.Add(new VerificationIssue
                        {
                            Severity = "Warning",
                            Kind     = "orphaned_transition",
                            Message  = $"... {orphanCount - maxOrphanIssues} additional orphaned transitions not listed.",
                        });
                    }
                    report.OrphanedTransitionCount = orphanCount;
                }
            }

            return report;
        }
    }
}
