using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// Session caa6cf50 gate-starvation fix (2026-06-11) — IME-side probe semantics.
    /// <see cref="ImeLogTracker.AreUserEspAppsSettled"/> must be conservative: it only returns
    /// <c>true</c> when the tracker is in the AccountSetup phase AND the live (current-phase)
    /// package list contains at least one required app, all required apps are terminal
    /// (Installed / Skipped / Postponed), and no tracked app is in Error.
    /// </summary>
    public sealed class ImeLogTrackerUserEspAppsSettledTests
    {
        private static ImeLogTracker BuildTracker(TempDirectory tmp) =>
            new ImeLogTracker(
                logFolder: tmp.Path,
                patterns: new List<ImeLogPattern>(),
                logger: new AgentLogger(tmp.Path, AgentLogLevel.Info));

        private static AppPackageState AddApp(
            ImeLogTracker tracker,
            string id,
            AppIntent intent,
            AppInstallationState state)
        {
            var pkg = tracker.PackageStates.GetPackage(id, createIfNotFound: true);
            pkg.UpdateIntent(intent);
            if (state == AppInstallationState.Installed)
            {
                // Installed requires DownloadingOrInstallingSeen, else UpdateState auto-downgrades
                // to Skipped (inverse-detection handling). Walk the real lifecycle.
                pkg.UpdateState(AppInstallationState.Installing);
            }
            if (state != AppInstallationState.Unknown)
            {
                pkg.UpdateState(state);
            }
            return pkg;
        }

        [Fact]
        public void False_outside_AccountSetup_phase()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp);
            tracker.SeedCurrentPhaseForTesting("DeviceSetup");
            AddApp(tracker, "app-1", AppIntent.Install, AppInstallationState.Installed);

            Assert.False(tracker.AreUserEspAppsSettled());
        }

        [Fact]
        public void False_when_phase_never_detected()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp);
            AddApp(tracker, "app-1", AppIntent.Install, AppInstallationState.Installed);

            Assert.False(tracker.AreUserEspAppsSettled());
        }

        [Fact]
        public void False_when_no_apps_tracked_in_current_phase()
        {
            // The live list is cleared on the DeviceSetup→AccountSetup transition; an empty list
            // (user apps not yet surfaced) must read as "not settled", never vacuously true —
            // otherwise the first intermediate esp_exiting would open the gate prematurely.
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp);
            tracker.SeedCurrentPhaseForTesting("AccountSetup");

            Assert.False(tracker.AreUserEspAppsSettled());
        }

        [Fact]
        public void False_while_a_required_app_is_still_in_flight()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp);
            tracker.SeedCurrentPhaseForTesting("AccountSetup");
            AddApp(tracker, "app-skipped", AppIntent.Install, AppInstallationState.Skipped);
            AddApp(tracker, "app-installing", AppIntent.Install, AppInstallationState.Installing);

            Assert.False(tracker.AreUserEspAppsSettled());
        }

        [Fact]
        public void False_when_any_app_failed()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp);
            tracker.SeedCurrentPhaseForTesting("AccountSetup");
            AddApp(tracker, "app-ok", AppIntent.Install, AppInstallationState.Installed);
            AddApp(tracker, "app-failed", AppIntent.Install, AppInstallationState.Error);

            Assert.False(tracker.AreUserEspAppsSettled());
        }

        [Fact]
        public void False_when_only_intent_unknown_apps_are_tracked()
        {
            // Intent never parsed → too weak as completion evidence; stay conservative.
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp);
            tracker.SeedCurrentPhaseForTesting("AccountSetup");
            AddApp(tracker, "app-unknown-intent", AppIntent.Unknown, AppInstallationState.Skipped);

            Assert.False(tracker.AreUserEspAppsSettled());
        }

        [Fact]
        public void True_when_all_required_user_apps_are_terminal_with_zero_failures()
        {
            // The caa6cf50 shape: one app policy-skipped (the registry's Apps subcategory
            // therefore never reaches "succeeded"), one app installed.
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp);
            tracker.SeedCurrentPhaseForTesting("AccountSetup");
            AddApp(tracker, "gsa-test", AppIntent.Install, AppInstallationState.Skipped);
            AddApp(tracker, "teamviewer", AppIntent.Install, AppInstallationState.Installed);

            Assert.True(tracker.AreUserEspAppsSettled());
        }

        [Fact]
        public void True_with_postponed_apps_counts_as_terminal()
        {
            // Postponed = IME deferred the install past ESP; the ESP page will not wait for it.
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp);
            tracker.SeedCurrentPhaseForTesting("AccountSetup");
            AddApp(tracker, "app-postponed", AppIntent.Install, AppInstallationState.Postponed);

            Assert.True(tracker.AreUserEspAppsSettled());
        }
    }
}
