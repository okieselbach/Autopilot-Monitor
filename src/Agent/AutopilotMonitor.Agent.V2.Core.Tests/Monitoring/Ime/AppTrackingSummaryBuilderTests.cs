#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// Locks down the <c>app_tracking_summary</c> data shape produced by
    /// <see cref="AppTrackingSummaryBuilder"/>. The Web's
    /// <c>useSessionDerivedData</c> / <c>useProgressDerivedData</c> hooks read these keys
    /// directly; the per-transition emit in <c>ImeLogTrackerAdapter</c> and the terminal
    /// emit in <c>EnrollmentTerminationHandler</c> share this builder.
    /// </summary>
    public sealed class AppTrackingSummaryBuilderTests
    {
        private static AppPackageState NewPkg(string id, AppTargeted targeted, AppInstallationState state)
        {
            var pkg = new AppPackageState(id, listPos: 0);
            // UpdateState's inverse-detection guard rewrites Installed→Skipped without a
            // prior Installing flip; lifecycle the package through Installing first.
            if (state == AppInstallationState.Installed)
                pkg.UpdateState(AppInstallationState.Installing);
            pkg.UpdateState(state);
            typeof(AppPackageState).GetProperty(nameof(AppPackageState.Targeted))!.SetValue(pkg, targeted);
            return pkg;
        }

        [Fact]
        public void Build_EmptyInputs_YieldsZeroAggregatesAndEmptyCollections()
        {
            var data = AppTrackingSummaryBuilder.Build(packages: null, timings: null);

            Assert.Equal(0, (int)data["totalApps"]);
            Assert.Equal(0, (int)data["completedApps"]);
            Assert.Equal(0, (int)data["installedApps"]);
            Assert.Equal(0, (int)data["failedApps"]);
            Assert.Equal(0, (int)data["downloading"]);
            Assert.Equal(0, (int)data["installing"]);
            Assert.Equal(0, (int)data["pending"]);

            var byPhase = (Dictionary<string, Dictionary<string, int>>)data["byPhase"];
            Assert.Empty(byPhase);

            var perApp = (List<Dictionary<string, object>>)data["perApp"];
            Assert.Empty(perApp);
        }

        [Fact]
        public void Build_MixedTerminalAndLiveStates_PopulatesAllBucketsCorrectly()
        {
            var packages = new List<AppPackageState>
            {
                NewPkg("a", AppTargeted.Device, AppInstallationState.Installed),
                NewPkg("b", AppTargeted.Device, AppInstallationState.Installed),
                NewPkg("c", AppTargeted.Device, AppInstallationState.Error),
                NewPkg("d", AppTargeted.User,   AppInstallationState.Skipped),
                NewPkg("e", AppTargeted.User,   AppInstallationState.Postponed),
                NewPkg("f", AppTargeted.Device, AppInstallationState.Downloading),
                NewPkg("g", AppTargeted.Device, AppInstallationState.Installing),
                NewPkg("h", AppTargeted.Device, AppInstallationState.NotInstalled),
            };

            var data = AppTrackingSummaryBuilder.Build(packages, timings: null);

            Assert.Equal(8, (int)data["totalApps"]);
            Assert.Equal(2, (int)data["installedApps"]);
            Assert.Equal(1, (int)data["failedApps"]);
            Assert.Equal(1, (int)data["skippedApps"]);
            Assert.Equal(1, (int)data["postponedApps"]);
            Assert.Equal(1, (int)data["downloading"]);
            Assert.Equal(1, (int)data["installing"]);
            Assert.Equal(5, (int)data["completedApps"]); // 2 installed + 1 failed + 1 skipped + 1 postponed
            Assert.Equal(1, (int)data["pending"]);       // total - completed - downloading - installing = 8-5-1-1
        }

        [Fact]
        public void Build_PendingNeverGoesNegative()
        {
            // Defensive: if state-machine ever feeds inconsistent data, pending is clamped at 0.
            var packages = new List<AppPackageState>
            {
                NewPkg("a", AppTargeted.Device, AppInstallationState.Installed),
                NewPkg("b", AppTargeted.Device, AppInstallationState.Downloading),
                NewPkg("c", AppTargeted.Device, AppInstallationState.Installing),
            };

            var data = AppTrackingSummaryBuilder.Build(packages, timings: null);

            Assert.Equal(0, (int)data["pending"]);
        }

        [Fact]
        public void Build_ByPhase_GroupsByTargetedAndCountsTerminals()
        {
            var packages = new List<AppPackageState>
            {
                NewPkg("a", AppTargeted.Device, AppInstallationState.Installed),
                NewPkg("b", AppTargeted.Device, AppInstallationState.Error),
                NewPkg("c", AppTargeted.User,   AppInstallationState.Installed),
                NewPkg("d", AppTargeted.User,   AppInstallationState.Skipped),
            };

            var data = AppTrackingSummaryBuilder.Build(packages, timings: null);
            var byPhase = (Dictionary<string, Dictionary<string, int>>)data["byPhase"];

            Assert.Equal(2, byPhase["Device"]["total"]);
            Assert.Equal(1, byPhase["Device"]["installed"]);
            Assert.Equal(1, byPhase["Device"]["failed"]);
            Assert.Equal(2, byPhase["User"]["total"]);
            Assert.Equal(1, byPhase["User"]["installed"]);
            Assert.Equal(1, byPhase["User"]["skipped"]);
        }

        [Fact]
        public void Build_LiveSchema_OmitsPerAppAndByPhase_PreservesCounters()
        {
            // Live snapshots (per-transition emit from ImeLogTrackerAdapter) intentionally
            // strip the per-app/per-phase detail to keep storage cost O(N) instead of O(N²).
            // The terminal emit in EnrollmentTerminationHandler is the single source of detail.
            var packages = new List<AppPackageState>
            {
                NewPkg("a", AppTargeted.Device, AppInstallationState.Installed),
                NewPkg("b", AppTargeted.Device, AppInstallationState.Error),
                NewPkg("c", AppTargeted.User,   AppInstallationState.Downloading),
            };

            var data = AppTrackingSummaryBuilder.Build(packages, timings: null, includePerAppDetail: false);

            // Detail keys must NOT be present.
            Assert.False(data.ContainsKey("perApp"));
            Assert.False(data.ContainsKey("byPhase"));

            // Counter keys must be present and correct.
            Assert.Equal(3, (int)data["totalApps"]);
            Assert.Equal(1, (int)data["installedApps"]);
            Assert.Equal(1, (int)data["failedApps"]);
            Assert.Equal(1, (int)data["downloading"]);
            Assert.Equal(2, (int)data["completedApps"]);
            Assert.Equal(0, (int)data["pending"]);
        }

        [Fact]
        public void Build_DefaultsToFullDetail_ForBackwardCompatibility()
        {
            // Callers that don't pass the flag (e.g. terminal emit, older code) get the
            // full schema with perApp/byPhase. Pinning the default here so a future
            // refactor doesn't silently flip behavior.
            var packages = new List<AppPackageState>
            {
                NewPkg("a", AppTargeted.Device, AppInstallationState.Installed),
            };

            var data = AppTrackingSummaryBuilder.Build(packages, timings: null);

            Assert.True(data.ContainsKey("perApp"));
            Assert.True(data.ContainsKey("byPhase"));
        }

        [Fact]
        public void Build_PerApp_IncludesTimingsWhenPresent()
        {
            var startedAt = new DateTime(2026, 4, 27, 10, 0, 0, DateTimeKind.Utc);
            var completedAt = startedAt.AddSeconds(42);
            var packages = new List<AppPackageState>
            {
                NewPkg("with-timing", AppTargeted.Device, AppInstallationState.Installed),
                NewPkg("no-timing",   AppTargeted.Device, AppInstallationState.Installed),
            };
            var timings = new Dictionary<string, AppInstallTiming>
            {
                ["with-timing"] = new AppInstallTiming(startedAt, completedAt),
            };

            var data = AppTrackingSummaryBuilder.Build(packages, timings);
            var perApp = (List<Dictionary<string, object>>)data["perApp"];

            Assert.Equal(2, perApp.Count);

            var withTiming = perApp.Find(e => (string)e["appId"] == "with-timing")!;
            Assert.Equal(startedAt.ToString("o"), withTiming["startedAt"]);
            Assert.Equal(completedAt.ToString("o"), withTiming["completedAt"]);
            Assert.Equal(42.0, (double)withTiming["durationSeconds"]);

            var noTiming = perApp.Find(e => (string)e["appId"] == "no-timing")!;
            Assert.False(noTiming.ContainsKey("startedAt"));
            Assert.False(noTiming.ContainsKey("completedAt"));
            Assert.False(noTiming.ContainsKey("durationSeconds"));
        }
    }
}
