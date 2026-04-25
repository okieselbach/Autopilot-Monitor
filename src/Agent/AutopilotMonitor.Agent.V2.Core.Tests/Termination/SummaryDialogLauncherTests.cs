#nullable enable
using System;
using AutopilotMonitor.Agent.V2.Core.Termination;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Termination
{
    /// <summary>
    /// Plan §F3 (debrief 7dd4e593) — `Path.GetTempPath()` resolves to
    /// <c>C:\WINDOWS\SystemTemp\</c> when the agent runs as SYSTEM. Standard users have no
    /// read/execute access there, so the SummaryDialog launched into the user session
    /// failed to start with a generic "This application could not be started" MessageBox.
    /// The launcher must place its temp directory under <c>%ProgramData%\AutopilotMonitor-Summary\</c>
    /// instead (V1 parity), which is world-readable + executable. Single flat directory
    /// (no per-launch GUID subdir) matches V1; the launcher wipes it on every launch.
    /// </summary>
    public sealed class SummaryDialogLauncherTests
    {
        [Fact]
        public void ResolveSummaryTempDir_returns_path_under_program_data()
        {
            var path = SummaryDialogLauncher.ResolveSummaryTempDir();
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            Assert.StartsWith(programData, path, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(SummaryDialogLauncher.SummaryTempRootEnvVar, path, StringComparison.Ordinal);
        }

        [Fact]
        public void ResolveSummaryTempDir_does_not_use_systemtemp_or_user_temp()
        {
            // Regression guard: the broken implementation used Path.GetTempPath() which
            // resolves to C:\WINDOWS\SystemTemp\ for SYSTEM. The new implementation must
            // never produce such a path even when the test process happens to run as SYSTEM.
            var path = SummaryDialogLauncher.ResolveSummaryTempDir();

            Assert.DoesNotContain(@"\WINDOWS\SystemTemp", path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"\AppData\Local\Temp", path, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ResolveSummaryTempDir_is_flat_no_per_launch_guid_subdir()
        {
            // V1 parity: a single flat directory wiped on every launch. The previous V2
            // implementation appended a per-launch Guid.NewGuid() subdir which (a) created
            // a leftover directory per session if --cleanup didn't fire and (b) diverged
            // from the V1 lifecycle that the dialog and ACL-grant helper both assume.
            var a = SummaryDialogLauncher.ResolveSummaryTempDir();
            var b = SummaryDialogLauncher.ResolveSummaryTempDir();

            Assert.Equal(a, b);
        }
    }
}
