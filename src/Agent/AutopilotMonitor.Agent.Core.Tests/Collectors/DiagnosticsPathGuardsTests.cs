using AutopilotMonitor.Agent.Core.Monitoring.Telemetry.Gather;
using Xunit;

namespace AutopilotMonitor.Agent.Core.Tests.Collectors
{
    /// <summary>
    /// Tests path validation for diagnostics log collection.
    /// Prevents: path traversal, privacy leaks via C:\Users, SAM hive exfiltration.
    /// </summary>
    public class DiagnosticsPathGuardsTests
    {
        // -- Allowed paths --

        [Fact]
        public void IsDiagnosticsPathAllowed_AutopilotMonitorLogs_True()
        {
            var result = DiagnosticsPathGuards.IsDiagnosticsPathAllowed(
                @"C:\ProgramData\AutopilotMonitor\Logs\agent.log");

            Assert.True(result);
        }

        [Fact]
        public void IsDiagnosticsPathAllowed_ImeLogs_True()
        {
            var result = DiagnosticsPathGuards.IsDiagnosticsPathAllowed(
                @"C:\ProgramData\Microsoft\IntuneManagementExtension\Logs\IntuneManagementExtension.log");

            Assert.True(result);
        }

        [Fact]
        public void IsDiagnosticsPathAllowed_WindowsPanther_True()
        {
            var result = DiagnosticsPathGuards.IsDiagnosticsPathAllowed(
                @"C:\Windows\Panther\setupact.log");

            Assert.True(result);
        }

        // -- Hard-blocked paths --

        [Fact]
        public void IsDiagnosticsPathAllowed_UsersDir_AlwaysBlocked()
        {
            // Must be blocked even in unrestricted mode (privacy protection)
            var result = DiagnosticsPathGuards.IsDiagnosticsPathAllowed(
                @"C:\Users\admin\Documents\secret.docx", unrestrictedMode: true);

            Assert.False(result);
        }

        [Fact]
        public void IsDiagnosticsPathAllowed_SystemConfig_Blocked()
        {
            // SAM, SECURITY, SYSTEM hives must always be blocked
            var result = DiagnosticsPathGuards.IsDiagnosticsPathAllowed(
                @"C:\Windows\System32\config\SAM", unrestrictedMode: true);

            Assert.False(result);
        }

        // -- Path traversal --

        [Fact]
        public void IsDiagnosticsPathAllowed_Traversal_Blocked()
        {
            // Traversal via ".." after allowed prefix must be caught by GetFullPath normalization
            var result = DiagnosticsPathGuards.IsDiagnosticsPathAllowed(
                @"C:\ProgramData\AutopilotMonitor\..\..\Users\admin\file.txt");

            Assert.False(result);
        }

        [Fact]
        public void IsDiagnosticsPathAllowed_EnvironmentVariable_Expanded()
        {
            // %ProgramData% should expand to C:\ProgramData
            var result = DiagnosticsPathGuards.IsDiagnosticsPathAllowed(
                @"%ProgramData%\AutopilotMonitor\Logs\agent.log");

            Assert.True(result);
        }

        // -- Edge cases --

        [Fact]
        public void IsDiagnosticsPathAllowed_WhitespaceOnly_Blocked()
        {
            Assert.False(DiagnosticsPathGuards.IsDiagnosticsPathAllowed("   "));
        }

        [Fact]
        public void IsDiagnosticsPathAllowed_NullOrEmpty_Blocked()
        {
            Assert.False(DiagnosticsPathGuards.IsDiagnosticsPathAllowed(null));
            Assert.False(DiagnosticsPathGuards.IsDiagnosticsPathAllowed(""));
        }

        [Fact]
        public void IsDiagnosticsPathAllowed_WildcardInFilename_Allowed()
        {
            // Wildcards in the last path segment should be supported
            var result = DiagnosticsPathGuards.IsDiagnosticsPathAllowed(
                @"C:\ProgramData\AutopilotMonitor\Logs\*.log");

            Assert.True(result);
        }

        [Fact]
        public void IsDiagnosticsPathAllowed_UnrestrictedMode_AllowsNonBlocked()
        {
            // In unrestricted mode, non-hard-blocked paths should be allowed
            var result = DiagnosticsPathGuards.IsDiagnosticsPathAllowed(
                @"C:\SomeRandomDir\logs\file.txt", unrestrictedMode: true);

            Assert.True(result);
        }

        [Fact]
        public void IsDiagnosticsPathAllowed_UnallowedDir_Blocked()
        {
            // Random directory not in allowlist should be blocked in normal mode
            var result = DiagnosticsPathGuards.IsDiagnosticsPathAllowed(
                @"C:\SomeRandomDir\logs\file.txt");

            Assert.False(result);
        }
    }
}
