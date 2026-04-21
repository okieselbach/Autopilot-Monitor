#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.Agent.V2.Core.Termination
{
    /// <summary>
    /// Composes a <see cref="FinalStatus"/> snapshot from the kernel <see cref="DecisionState"/>
    /// plus the IME tracker's live <see cref="AppPackageStateList"/>. Plan §4.x M4.6.β.
    /// <para>
    /// Pure function: no I/O, no side effects. The writer (<see cref="SummaryDialogLauncher"/>)
    /// takes the built DTO and serialises it to JSON at the path the dialog reads. Splitting
    /// builder from writer keeps this testable without touching the file system.
    /// </para>
    /// </summary>
    public static class FinalStatusBuilder
    {
        /// <summary>
        /// Constructs a <see cref="FinalStatus"/> from the orchestrator outcome plus a live snapshot
        /// of the IME package state list (may be <c>null</c> when the IME host was not started —
        /// the summary then reports an empty app section).
        /// </summary>
        public static FinalStatus Build(
            DecisionState state,
            EnrollmentTerminatedEventArgs terminated,
            AppPackageStateList? packageStates,
            DateTime agentStartTimeUtc)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (terminated == null) throw new ArgumentNullException(nameof(terminated));

            var uptimeSeconds = Math.Max(0, (terminated.TerminatedAtUtc - agentStartTimeUtc).TotalSeconds);

            var status = new FinalStatus
            {
                Timestamp = terminated.TerminatedAtUtc.ToString("O"),
                Outcome = MapOutcome(terminated.Outcome, state.Stage),
                CompletionSource = terminated.Reason.ToString(),
                HelloOutcome = state.HelloOutcome?.Value ?? "unknown",
                EnrollmentType = state.EnrollmentType?.Reason ?? state.EnrollmentType?.Level.ToString() ?? "unknown",
                AgentUptimeSeconds = uptimeSeconds,
                SignalsSeen = BuildSignalsSeen(state),
                AppSummary = BuildAppSummary(packageStates),
                PackageStatesByPhase = BuildPackageStatesByPhase(packageStates),
            };

            return status;
        }

        private static string MapOutcome(EnrollmentTerminationOutcome outcome, SessionStage stage)
        {
            if (stage == SessionStage.WhiteGloveSealed) return "whiteglove_part1";
            if (stage == SessionStage.WhiteGloveCompletedPart2) return "whiteglove_part2";
            switch (outcome)
            {
                case EnrollmentTerminationOutcome.Succeeded: return "succeeded";
                case EnrollmentTerminationOutcome.Failed: return "failed";
                case EnrollmentTerminationOutcome.TimedOut: return "timed_out";
                default: return "unknown";
            }
        }

        private static List<string> BuildSignalsSeen(DecisionState state)
        {
            var signals = new List<string>();
            if (state.EspFinalExitUtc != null) signals.Add("esp_final_exit");
            if (state.HelloResolvedUtc != null) signals.Add("hello_resolved");
            if (state.DesktopArrivedUtc != null) signals.Add("desktop_arrived");
            if (state.SystemRebootUtc != null) signals.Add("system_reboot");
            if (state.AadJoinedWithUser != null && state.AadJoinedWithUser.Value) signals.Add("aad_user_joined");
            if (state.ShellCoreWhiteGloveSuccessSeen != null && state.ShellCoreWhiteGloveSuccessSeen.Value)
                signals.Add("whiteglove_shellcore_success");
            if (state.WhiteGloveSealingPatternSeen != null && state.WhiteGloveSealingPatternSeen.Value)
                signals.Add("whiteglove_sealing_pattern");
            if (state.ImeMatchedPatternId != null) signals.Add($"ime_pattern:{state.ImeMatchedPatternId.Value}");
            if (state.UserAadSignInCompleteUtc != null) signals.Add("part2_user_aad_signin");
            if (state.HelloResolvedPart2Utc != null) signals.Add("part2_hello_resolved");
            if (state.DesktopArrivedPart2Utc != null) signals.Add("part2_desktop_arrived");
            if (state.AccountSetupCompletedPart2Utc != null) signals.Add("part2_account_setup_completed");
            return signals;
        }

        private static FinalStatusAppSummary BuildAppSummary(AppPackageStateList? packageStates)
        {
            var summary = new FinalStatusAppSummary();
            if (packageStates == null || packageStates.Count == 0) return summary;

            foreach (var pkg in packageStates)
            {
                summary.TotalApps++;
                if (IsCompleted(pkg.InstallationState)) summary.CompletedApps++;
                if (pkg.InstallationState == AppInstallationState.Error)
                {
                    summary.ErrorCount++;
                    if (pkg.Targeted == AppTargeted.Device) summary.DeviceErrors++;
                    else if (pkg.Targeted == AppTargeted.User) summary.UserErrors++;
                }

                var phaseKey = pkg.Targeted.ToString();
                if (!summary.AppsByPhase.TryGetValue(phaseKey, out var cnt)) cnt = 0;
                summary.AppsByPhase[phaseKey] = cnt + 1;
            }

            return summary;
        }

        private static Dictionary<string, List<FinalStatusPackageInfo>> BuildPackageStatesByPhase(
            AppPackageStateList? packageStates)
        {
            var result = new Dictionary<string, List<FinalStatusPackageInfo>>();
            if (packageStates == null) return result;

            foreach (var pkg in packageStates)
            {
                var phaseKey = pkg.Targeted.ToString();
                if (!result.TryGetValue(phaseKey, out var bucket))
                {
                    bucket = new List<FinalStatusPackageInfo>();
                    result[phaseKey] = bucket;
                }

                bucket.Add(new FinalStatusPackageInfo
                {
                    AppName = string.IsNullOrEmpty(pkg.Name) ? pkg.Id : pkg.Name,
                    State = pkg.InstallationState.ToString(),
                    IsError = pkg.InstallationState == AppInstallationState.Error,
                    IsCompleted = IsCompleted(pkg.InstallationState),
                    Targeted = pkg.Targeted.ToString(),
                });
            }

            return result;
        }

        private static bool IsCompleted(AppInstallationState s) =>
            s == AppInstallationState.Installed
            || s == AppInstallationState.Skipped
            || s == AppInstallationState.Postponed
            || s == AppInstallationState.Error;
    }
}
