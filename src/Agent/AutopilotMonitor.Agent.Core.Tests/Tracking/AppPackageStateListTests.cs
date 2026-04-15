using System.Collections.Generic;
using System.Linq;
using Xunit;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Flows;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Completion;

namespace AutopilotMonitor.Agent.Core.Tests.Tracking
{
    /// <summary>
    /// Tests phase isolation (ignore list), dependency cascading, completion detection,
    /// and circular dependency protection.
    /// Prevents: phase bleed, broken dependency chains, infinite recursion.
    /// </summary>
    public class AppPackageStateListTests
    {
        private static AppPackageStateList CreateList()
            => new AppPackageStateList(null); // null logger is fine for tests

        // -- Ignore list (phase isolation) --

        [Fact]
        public void GetPackage_InIgnoreList_ReturnsNull()
        {
            var list = CreateList();
            list.Add(new AppPackageState("app-001", 0));
            list.IgnoreList.Add("app-001");

            var result = list.GetPackage("app-001");

            Assert.Null(result);
        }

        [Fact]
        public void IgnoreList_CaseInsensitive()
        {
            var list = CreateList();
            list.Add(new AppPackageState("abc-123-DEF", 0));
            list.IgnoreList.Add("abc-123-def");

            var result = list.GetPackage("ABC-123-DEF");

            Assert.Null(result);
        }

        [Fact]
        public void GetPackage_NotInIgnoreList_ReturnsPackage()
        {
            var list = CreateList();
            list.Add(new AppPackageState("app-001", 0));

            var result = list.GetPackage("app-001");

            Assert.NotNull(result);
            Assert.Equal("app-001", result.Id);
        }

        [Fact]
        public void GetPackage_CreateIfNotFound_CreatesNew()
        {
            var list = CreateList();

            var result = list.GetPackage("new-app", createIfNotFound: true);

            Assert.NotNull(result);
            Assert.Equal("new-app", result.Id);
            Assert.Single(list);
        }

        [Fact]
        public void GetPackage_NullId_ReturnsNull()
        {
            var list = CreateList();

            Assert.Null(list.GetPackage(null));
            Assert.Null(list.GetPackage(""));
        }

        // -- Dependency cascading --

        [Fact]
        public void UpdateState_ErrorCascadesToDependents()
        {
            var list = CreateList();
            var appA = new AppPackageState("app-a", 0);
            appA.UpdateDependsOn(new HashSet<string> { "app-b" });
            var appB = new AppPackageState("app-b", 1);
            list.Add(appA);
            list.Add(appB);

            list.UpdateState("app-b", AppInstallationState.Error);

            Assert.Equal(AppInstallationState.Error, appB.InstallationState);
            Assert.Equal(AppInstallationState.Error, appA.InstallationState);
        }

        [Fact]
        public void UpdateState_PostponedCascadesToDependents()
        {
            var list = CreateList();
            var appA = new AppPackageState("app-a", 0);
            appA.UpdateDependsOn(new HashSet<string> { "app-b" });
            var appB = new AppPackageState("app-b", 1);
            list.Add(appA);
            list.Add(appB);

            list.UpdateState("app-b", AppInstallationState.Postponed);

            Assert.Equal(AppInstallationState.Postponed, appA.InstallationState);
        }

        [Fact]
        public void UpdateState_InstalledDoesNotCascade()
        {
            var list = CreateList();
            var appA = new AppPackageState("app-a", 0);
            appA.UpdateIntent(AppIntent.Install);
            appA.UpdateDependsOn(new HashSet<string> { "app-b" });
            var appB = new AppPackageState("app-b", 1);
            appB.UpdateIntent(AppIntent.Install);
            appB.UpdateState(AppInstallationState.Downloading);
            list.Add(appA);
            list.Add(appB);

            list.UpdateState("app-b", AppInstallationState.Installed);

            // appA should NOT be cascaded — Installed does not cascade
            Assert.NotEqual(AppInstallationState.Installed, appA.InstallationState);
        }

        [Fact]
        public void UpdateState_DeepCascade_TwoLevels()
        {
            var list = CreateList();
            var appA = new AppPackageState("app-a", 0);
            appA.UpdateDependsOn(new HashSet<string> { "app-b" });
            var appB = new AppPackageState("app-b", 1);
            appB.UpdateDependsOn(new HashSet<string> { "app-c" });
            var appC = new AppPackageState("app-c", 2);
            list.AddRange(new[] { appA, appB, appC });

            list.UpdateState("app-c", AppInstallationState.Error);

            Assert.Equal(AppInstallationState.Error, appC.InstallationState);
            Assert.Equal(AppInstallationState.Error, appB.InstallationState);
            Assert.Equal(AppInstallationState.Error, appA.InstallationState);
        }

        [Fact]
        public void GetDependentIdsDeep_CircularDependency_DoesNotStackOverflow()
        {
            // A depends on B, B depends on A — must not infinite loop
            var list = CreateList();
            var appA = new AppPackageState("app-a", 0);
            appA.UpdateDependsOn(new HashSet<string> { "app-b" });
            var appB = new AppPackageState("app-b", 1);
            appB.UpdateDependsOn(new HashSet<string> { "app-a" });
            list.Add(appA);
            list.Add(appB);

            // This exercises GetDependentIdsDeep indirectly — should not throw
            list.UpdateState("app-a", AppInstallationState.Error);

            Assert.Equal(AppInstallationState.Error, appA.InstallationState);
            Assert.Equal(AppInstallationState.Error, appB.InstallationState);
        }

        // -- Completion detection --

        [Fact]
        public void IsAllCompleted_AllRequiredInstalled_True()
        {
            var list = CreateList();
            var pkg = new AppPackageState("app-1", 0);
            pkg.UpdateIntent(AppIntent.Install);
            pkg.UpdateState(AppInstallationState.Downloading);
            pkg.UpdateState(AppInstallationState.Installed);
            list.Add(pkg);

            Assert.True(list.IsAllCompleted());
        }

        [Fact]
        public void IsAllCompleted_RequiredStillPending_False()
        {
            var list = CreateList();
            var pkg = new AppPackageState("app-1", 0);
            pkg.UpdateIntent(AppIntent.Install);
            pkg.UpdateState(AppInstallationState.Downloading);
            list.Add(pkg);

            Assert.False(list.IsAllCompleted());
        }

        [Fact]
        public void IsAllCompleted_Empty_ReturnsFalse()
        {
            var list = CreateList();

            Assert.False(list.IsAllCompleted());
        }

        [Fact]
        public void IsAllCompleted_DependencyOnlyNotTouched_AutoSkipped()
        {
            var list = CreateList();

            // Required app: installed
            var required = new AppPackageState("req-app", 0);
            required.UpdateIntent(AppIntent.Install);
            required.UpdateState(AppInstallationState.Downloading);
            required.UpdateState(AppInstallationState.Installed);
            list.Add(required);

            // Dependency-only app: never touched by IME
            var depOnly = new AppPackageState("dep-app", 1);
            depOnly.UpdateIntent(AppIntent.Available); // Not required
            list.Add(depOnly);

            var result = list.IsAllCompleted();

            Assert.True(result);
            // Dependency-only app should be auto-skipped
            Assert.Equal(AppInstallationState.Skipped, depOnly.InstallationState);
        }

        // -- Download state guards --

        [Fact]
        public void UpdateStateToDownloading_SkippedApp_Ignored()
        {
            var list = CreateList();
            var pkg = new AppPackageState("app-1", 0);
            // Force to Skipped (without downloading first = auto-skip)
            pkg.UpdateState(AppInstallationState.Installed); // becomes Skipped
            list.Add(pkg);

            var changed = list.UpdateStateToDownloading("app-1", "100", "1000");

            Assert.False(changed);
            Assert.Equal(AppInstallationState.Skipped, pkg.InstallationState);
        }

        [Fact]
        public void UpdateStateToDownloading_NormalApp_CalculatesProgress()
        {
            var list = CreateList();
            var pkg = new AppPackageState("app-1", 0);
            list.Add(pkg);

            list.UpdateStateToDownloading("app-1", "500", "1000");

            Assert.Equal(AppInstallationState.Downloading, pkg.InstallationState);
            Assert.Equal(50, pkg.ProgressPercent);
            Assert.Equal(500, pkg.BytesDownloaded);
            Assert.Equal(1000, pkg.BytesTotal);
        }

        // -- AddToIgnoreList --

        [Fact]
        public void AddToIgnoreList_NullOrEmpty_ReturnsFalse()
        {
            var list = CreateList();

            Assert.False(list.AddToIgnoreList(null));
            Assert.False(list.AddToIgnoreList(""));
        }

        [Fact]
        public void AddToIgnoreList_Duplicate_ReturnsFalse()
        {
            var list = CreateList();
            list.AddToIgnoreList("app-1");

            Assert.False(list.AddToIgnoreList("app-1"));
        }
    }
}
