#nullable enable
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// F5 (debrief 7dd4e593) — V2 clears <c>_packageStates</c> on the DeviceSetup→AccountSetup
    /// ESP transition (intentional, prevents the IgnoreList from growing unboundedly). The
    /// termination summary path therefore must read the deduped union of phase snapshots and
    /// the live list. <see cref="ImeLogTracker.GetAllKnownPackageStates"/> is the consolidated
    /// view consumed by <c>FinalStatusBuilder</c> and <c>app_tracking_summary</c>.
    /// </summary>
    public sealed class ImeLogTrackerPhaseSnapshotTests
    {
        private static AppPackageState NewPkg(string id, AppTargeted targeted, AppInstallationState terminal)
        {
            var pkg = new AppPackageState(id, listPos: 0);
            // UpdateState's inverse-detection guard rewrites Installed→Skipped without a
            // prior Installing flip; lifecycle the package through Installing first so the
            // terminal sticks (mirrors the live IME log flow).
            if (terminal == AppInstallationState.Installed)
                pkg.UpdateState(AppInstallationState.Installing);
            pkg.UpdateState(terminal);
            typeof(AppPackageState).GetProperty(nameof(AppPackageState.Targeted))!.SetValue(pkg, targeted);
            return pkg;
        }

        private static ImeLogTracker BuildTracker(TempDirectory tmp) =>
            new ImeLogTracker(
                logFolder: tmp.Path,
                patterns: new List<ImeLogPattern>(),
                logger: new AgentLogger(tmp.Path, AgentLogLevel.Info));

        [Fact]
        public void GetAllKnownPackageStates_empty_when_no_snapshot_and_no_live()
        {
            using var tmp = new TempDirectory();
            using var tracker = BuildTracker(tmp);

            Assert.Empty(tracker.GetAllKnownPackageStates());
        }

        [Fact]
        public void GetAllKnownPackageStates_returns_live_packages_only()
        {
            using var tmp = new TempDirectory();
            using var tracker = BuildTracker(tmp);

            tracker.PackageStates.Add(NewPkg("user-1", AppTargeted.User, AppInstallationState.Installed));
            tracker.PackageStates.Add(NewPkg("user-2", AppTargeted.User, AppInstallationState.Installed));

            var all = tracker.GetAllKnownPackageStates();

            Assert.Equal(2, all.Count);
            Assert.Contains(all, p => p.Id == "user-1");
            Assert.Contains(all, p => p.Id == "user-2");
        }

        [Fact]
        public void GetAllKnownPackageStates_returns_snapshot_packages_when_live_is_empty()
        {
            // Reproduces the live-session scenario: at the AccountSetup transition the tracker
            // moved 8 DeviceSetup apps into the snapshot dict and cleared _packageStates. If
            // no user-phase apps had been discovered yet the live list is empty, but the
            // SummaryDialog must still show the 8 DeviceSetup apps.
            using var tmp = new TempDirectory();
            using var tracker = BuildTracker(tmp);

            tracker.SeedPhaseSnapshotForTesting("DeviceSetup", new[]
            {
                NewPkg("device-1", AppTargeted.Device, AppInstallationState.Installed),
                NewPkg("device-2", AppTargeted.Device, AppInstallationState.Installed),
            });

            var all = tracker.GetAllKnownPackageStates();

            Assert.Equal(2, all.Count);
            Assert.All(all, p => Assert.Equal(AppTargeted.Device, p.Targeted));
        }

        [Fact]
        public void GetAllKnownPackageStates_returns_union_of_snapshot_and_live()
        {
            // Live-session scenario from session 7dd4e593: 8 Device apps in the snapshot
            // (post-clear), 3 User apps in the live list. Termination summary expects 11.
            using var tmp = new TempDirectory();
            using var tracker = BuildTracker(tmp);

            tracker.SeedPhaseSnapshotForTesting("DeviceSetup", new[]
            {
                NewPkg("device-1", AppTargeted.Device, AppInstallationState.Installed),
                NewPkg("device-2", AppTargeted.Device, AppInstallationState.Installed),
            });
            tracker.PackageStates.Add(NewPkg("user-1", AppTargeted.User, AppInstallationState.Installed));
            tracker.PackageStates.Add(NewPkg("user-2", AppTargeted.User, AppInstallationState.Installed));
            tracker.PackageStates.Add(NewPkg("user-3", AppTargeted.User, AppInstallationState.Installed));

            var all = tracker.GetAllKnownPackageStates();

            Assert.Equal(5, all.Count);
            Assert.Equal(2, all.Count(p => p.Targeted == AppTargeted.Device));
            Assert.Equal(3, all.Count(p => p.Targeted == AppTargeted.User));
        }

        [Fact]
        public void GetAllKnownPackageStates_dedupes_by_id_with_live_winning()
        {
            // Defensive: ESP-phase moves all known IDs into IgnoreList so an app cannot
            // reappear in _packageStates under the same Id, but if a future code path ever
            // re-adds an Id present in a snapshot the live entry must win — its
            // InstallationState reflects the most recent observation.
            using var tmp = new TempDirectory();
            using var tracker = BuildTracker(tmp);

            tracker.SeedPhaseSnapshotForTesting("DeviceSetup", new[]
            {
                NewPkg("shared-id", AppTargeted.Device, AppInstallationState.Error),
            });
            tracker.PackageStates.Add(NewPkg("shared-id", AppTargeted.User, AppInstallationState.Installed));

            var all = tracker.GetAllKnownPackageStates();

            var entry = Assert.Single(all);
            Assert.Equal(AppTargeted.User, entry.Targeted);
            Assert.Equal(AppInstallationState.Installed, entry.InstallationState);
        }
    }
}
