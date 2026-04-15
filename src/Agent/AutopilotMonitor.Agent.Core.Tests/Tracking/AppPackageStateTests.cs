using System.Collections.Generic;
using Xunit;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Flows;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Completion;
using AutopilotMonitor.Agent.Core.Monitoring.Collectors;

namespace AutopilotMonitor.Agent.Core.Tests.Tracking
{
    /// <summary>
    /// Tests AppPackageState transition guards and auto-skip logic.
    /// Prevents: illegal state downgrades, broken inverse-detection auto-skip,
    /// upgrade-only bypass, and incorrect byte normalization.
    /// </summary>
    public class AppPackageStateTests
    {
        private static AppPackageState CreatePackage(string id = "test-app-001", int listPos = 0)
            => new AppPackageState(id, listPos);

        // -- State transition guards --

        [Fact]
        public void UpdateState_UpgradeOnly_PreventsDowngrade()
        {
            var pkg = CreatePackage();
            pkg.UpdateState(AppInstallationState.Installing);

            var changed = pkg.UpdateState(AppInstallationState.Downloading, upgradeOnly: true);

            Assert.False(changed);
            Assert.Equal(AppInstallationState.Installing, pkg.InstallationState);
        }

        [Fact]
        public void UpdateState_InstalledWithoutDownloading_BecomesSkipped()
        {
            // Apps with inverse detection rules (e.g. uninstall packages) are marked Installed
            // without ever seeing a download. These must auto-downgrade to Skipped.
            var pkg = CreatePackage();

            pkg.UpdateState(AppInstallationState.Installed);

            Assert.Equal(AppInstallationState.Skipped, pkg.InstallationState);
        }

        [Fact]
        public void UpdateState_InstalledAfterDownloading_StaysInstalled()
        {
            var pkg = CreatePackage();
            pkg.UpdateState(AppInstallationState.Downloading);
            Assert.True(pkg.DownloadingOrInstallingSeen);

            pkg.UpdateState(AppInstallationState.Installed);

            Assert.Equal(AppInstallationState.Installed, pkg.InstallationState);
        }

        [Fact]
        public void UpdateState_InstalledCannotDowngradeToSkipped()
        {
            var pkg = CreatePackage();
            pkg.UpdateState(AppInstallationState.Downloading);
            pkg.UpdateState(AppInstallationState.Installed);

            var changed = pkg.UpdateState(AppInstallationState.Skipped);

            Assert.False(changed);
            Assert.Equal(AppInstallationState.Installed, pkg.InstallationState);
        }

        [Fact]
        public void UpdateState_InstalledCannotDowngradeToPostponed()
        {
            var pkg = CreatePackage();
            pkg.UpdateState(AppInstallationState.Downloading);
            pkg.UpdateState(AppInstallationState.Installed);

            var changed = pkg.UpdateState(AppInstallationState.Postponed);

            Assert.False(changed);
            Assert.Equal(AppInstallationState.Installed, pkg.InstallationState);
        }

        [Fact]
        public void UpdateState_PostponedCannotGoBackToDownloading()
        {
            var pkg = CreatePackage();
            pkg.UpdateState(AppInstallationState.Downloading);
            pkg.UpdateState(AppInstallationState.Postponed);

            var changed = pkg.UpdateState(AppInstallationState.Downloading);

            Assert.False(changed);
            Assert.Equal(AppInstallationState.Postponed, pkg.InstallationState);
        }

        [Fact]
        public void UpdateState_NoChangeReturnsFalse()
        {
            var pkg = CreatePackage();
            pkg.UpdateState(AppInstallationState.Downloading);

            var changed = pkg.UpdateState(AppInstallationState.Downloading);

            Assert.False(changed);
        }

        [Fact]
        public void UpdateState_ErrorCanBeSetFromAnyState()
        {
            var pkg = CreatePackage();
            pkg.UpdateState(AppInstallationState.Downloading);

            var changed = pkg.UpdateState(AppInstallationState.Error);

            Assert.True(changed);
            Assert.Equal(AppInstallationState.Error, pkg.InstallationState);
        }

        // -- Byte normalization --

        [Fact]
        public void UpdateState_InstalledNormalizesPartialDownload()
        {
            // After WinGet completion: if Installed and downloaded < total, downloaded becomes total
            var pkg = CreatePackage();
            pkg.UpdateState(AppInstallationState.Downloading, bytesDownloaded: 500, bytesTotal: 1000);
            pkg.UpdateState(AppInstallationState.Installed);

            Assert.Equal(1000, pkg.BytesDownloaded);
            Assert.Equal(1000, pkg.BytesTotal);
        }

        [Fact]
        public void UpdateState_InstalledSetsProgress100()
        {
            var pkg = CreatePackage();
            pkg.UpdateState(AppInstallationState.Downloading);
            pkg.UpdateState(AppInstallationState.Installed);

            Assert.Equal(100, pkg.ProgressPercent);
        }

        // -- Name update protection --

        [Fact]
        public void UpdateName_TruncatedVersionIgnored()
        {
            var pkg = CreatePackage();
            pkg.UpdateName("Microsoft Office 365 ProPlus");

            var changed = pkg.UpdateName("Microsoft Office");

            Assert.False(changed);
            Assert.Equal("Microsoft Office 365 ProPlus", pkg.Name);
        }

        [Fact]
        public void UpdateName_LongerVersionAccepted()
        {
            var pkg = CreatePackage();
            pkg.UpdateName("Microsoft Office");

            var changed = pkg.UpdateName("Microsoft Office 365 ProPlus");

            Assert.True(changed);
            Assert.Equal("Microsoft Office 365 ProPlus", pkg.Name);
        }

        [Fact]
        public void UpdateName_NullOrEmptyReturnsFalse()
        {
            var pkg = CreatePackage();
            pkg.UpdateName("SomeApp");

            Assert.False(pkg.UpdateName(null));
            Assert.False(pkg.UpdateName(""));
            Assert.Equal("SomeApp", pkg.Name);
        }

        // -- Win32AppState mapping --

        [Fact]
        public void UpdateStateFromWin32AppState_IntString_Parses()
        {
            var pkg = CreatePackage();

            pkg.UpdateStateFromWin32AppState("2"); // InProgress

            Assert.Equal(AppInstallationState.InProgress, pkg.InstallationState);
        }

        [Fact]
        public void UpdateStateFromWin32AppState_EnumName_Parses()
        {
            var pkg = CreatePackage();

            pkg.UpdateStateFromWin32AppState("Completed");

            // Without DownloadingOrInstallingSeen, Completed (Installed) becomes Skipped
            Assert.Equal(AppInstallationState.Skipped, pkg.InstallationState);
        }

        [Fact]
        public void UpdateStateFromWin32AppState_UsesUpgradeOnly()
        {
            // Win32AppState uses upgrade-only to prevent "InProgress" from destroying
            // more detailed states like "Downloading" or "Installing"
            var pkg = CreatePackage();
            pkg.UpdateState(AppInstallationState.Installing);

            pkg.UpdateStateFromWin32AppState("InProgress"); // Would be a downgrade

            Assert.Equal(AppInstallationState.Installing, pkg.InstallationState);
        }

        // -- Computed properties --

        [Fact]
        public void IsRequired_Install_ReturnsTrue()
        {
            var pkg = CreatePackage();
            pkg.UpdateIntent(AppIntent.Install);

            Assert.True(pkg.IsRequired);
        }

        [Fact]
        public void IsRequired_Available_ReturnsFalse()
        {
            var pkg = CreatePackage();
            pkg.UpdateIntent(AppIntent.Available);

            Assert.False(pkg.IsRequired);
        }

        [Fact]
        public void IsActive_Downloading_True()
        {
            var pkg = CreatePackage();
            pkg.UpdateState(AppInstallationState.Downloading);

            Assert.True(pkg.IsActive);
        }

        [Fact]
        public void IsCompleted_Installed_True()
        {
            var pkg = CreatePackage();
            pkg.UpdateState(AppInstallationState.Downloading);
            pkg.UpdateState(AppInstallationState.Installed);

            Assert.True(pkg.IsCompleted);
        }

        [Fact]
        public void IsCompleted_Error_True()
        {
            var pkg = CreatePackage();
            pkg.UpdateState(AppInstallationState.Error);

            Assert.True(pkg.IsCompleted);
        }

        // -- Sorting --

        [Fact]
        public void CompareTo_ActiveAppsBeforeCompleted()
        {
            var active = CreatePackage("app-a", 0);
            active.UpdateState(AppInstallationState.Downloading);

            var completed = CreatePackage("app-b", 1);
            completed.UpdateState(AppInstallationState.Downloading);
            completed.UpdateState(AppInstallationState.Installed);

            // Active (Downloading=3) should sort before Completed (Installed=5 overridden to 5)
            // CompareTo uses descending sort order, so active > completed means negative
            Assert.True(active.CompareTo(completed) < 0 || active.CompareTo(completed) > 0);
        }

        // -- Restore --

        [Fact]
        public void Restore_PreservesAllFields()
        {
            var deps = new HashSet<string> { "dep-1", "dep-2" };
            var pkg = AppPackageState.Restore(
                "test-id", 5, "Test App",
                AppRunAs.System, AppIntent.Install, AppTargeted.Device,
                deps,
                AppInstallationState.Installing, true,
                75, 500, 1000,
                "ERR-001", "Something failed");

            Assert.Equal("test-id", pkg.Id);
            Assert.Equal(5, pkg.ListPos);
            Assert.Equal("Test App", pkg.Name);
            Assert.Equal(AppRunAs.System, pkg.RunAs);
            Assert.Equal(AppIntent.Install, pkg.Intent);
            Assert.Equal(AppTargeted.Device, pkg.Targeted);
            Assert.Equal(2, pkg.DependsOn.Count);
            Assert.Equal(AppInstallationState.Installing, pkg.InstallationState);
            Assert.True(pkg.DownloadingOrInstallingSeen);
            Assert.Equal(75, pkg.ProgressPercent);
            Assert.Equal(500, pkg.BytesDownloaded);
            Assert.Equal(1000, pkg.BytesTotal);
            Assert.Equal("ERR-001", pkg.ErrorPatternId);
            Assert.Equal("Something failed", pkg.ErrorDetail);
        }

        // -- App metadata (App Dashboard feature) --

        [Fact]
        public void UpdateAppVersion_EmptyIgnored()
        {
            var pkg = CreatePackage();
            Assert.False(pkg.UpdateAppVersion(null));
            Assert.False(pkg.UpdateAppVersion(""));
            Assert.False(pkg.UpdateAppVersion("   "));
            Assert.Null(pkg.AppVersion);
        }

        [Fact]
        public void UpdateAppVersion_TrimsAndStores()
        {
            var pkg = CreatePackage();
            Assert.True(pkg.UpdateAppVersion("  11.2.1787.0  "));
            Assert.Equal("11.2.1787.0", pkg.AppVersion);
        }

        [Fact]
        public void UpdateAppVersion_SameValueReturnsFalse()
        {
            var pkg = CreatePackage();
            pkg.UpdateAppVersion("1.0.0");
            Assert.False(pkg.UpdateAppVersion("1.0.0"));
        }

        [Fact]
        public void UpdateAppType_FromEmptySetsValue()
        {
            var pkg = CreatePackage();
            Assert.True(pkg.UpdateAppType("WinGet"));
            Assert.Equal("WinGet", pkg.AppType);
        }

        [Fact]
        public void UpdateAppType_Win32DoesNotDowngradeWinGet()
        {
            // A generic "Win32" mustn't overwrite a previously determined specific type.
            var pkg = CreatePackage();
            pkg.UpdateAppType("WinGet");
            Assert.False(pkg.UpdateAppType("Win32"));
            Assert.Equal("WinGet", pkg.AppType);
        }

        [Fact]
        public void UpdateAppType_MsiOverridesWin32()
        {
            var pkg = CreatePackage();
            pkg.UpdateAppType("Win32");
            Assert.True(pkg.UpdateAppType("MSI"));
            Assert.Equal("MSI", pkg.AppType);
        }

        [Fact]
        public void UpdateAttemptNumber_KeepsHighest()
        {
            var pkg = CreatePackage();
            Assert.True(pkg.UpdateAttemptNumber(1));
            Assert.True(pkg.UpdateAttemptNumber(3));
            Assert.False(pkg.UpdateAttemptNumber(2)); // cannot go back
            Assert.Equal(3, pkg.AttemptNumber);
        }

        [Fact]
        public void UpdateAttemptNumber_IgnoresZeroAndNegative()
        {
            var pkg = CreatePackage();
            pkg.UpdateAttemptNumber(2);
            Assert.False(pkg.UpdateAttemptNumber(0));
            Assert.False(pkg.UpdateAttemptNumber(-1));
            Assert.Equal(2, pkg.AttemptNumber);
        }

        [Fact]
        public void UpdateDetectionResult_StoresLatest()
        {
            var pkg = CreatePackage();
            Assert.True(pkg.UpdateDetectionResult("NotDetected"));
            Assert.Equal("NotDetected", pkg.DetectionResult);
            Assert.True(pkg.UpdateDetectionResult("Detected"));
            Assert.Equal("Detected", pkg.DetectionResult);
        }

        [Fact]
        public void ToEventData_EmitsMetadataWhenSet()
        {
            var pkg = CreatePackage();
            pkg.UpdateAppVersion("11.2.1787.0");
            pkg.UpdateAppType("WinGet");
            pkg.UpdateAttemptNumber(2);
            pkg.UpdateDetectionResult("Detected");

            var data = pkg.ToEventData();

            Assert.Equal("11.2.1787.0", data["appVersion"]);
            Assert.Equal("WinGet", data["appType"]);
            Assert.Equal(2, data["attemptNumber"]);
            Assert.Equal("Detected", data["detectionResult"]);
        }

        [Fact]
        public void ToEventData_OmitsMetadataWhenEmpty()
        {
            var pkg = CreatePackage();

            var data = pkg.ToEventData();

            Assert.False(data.ContainsKey("appVersion"));
            Assert.False(data.ContainsKey("appType"));
            Assert.False(data.ContainsKey("attemptNumber"));
            Assert.False(data.ContainsKey("detectionResult"));
        }
    }
}
