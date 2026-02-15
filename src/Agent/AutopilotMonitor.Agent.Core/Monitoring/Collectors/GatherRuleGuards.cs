using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AutopilotMonitor.Agent.Core.Monitoring.Collectors
{
    /// <summary>
    /// Privacy and security guards for GatherRule collectors.
    ///
    /// All three allowlists (registry, file, WMI) restrict data collection to
    /// enrollment-relevant information only. Rules targeting paths or queries
    /// outside these lists are blocked and reported as security_warning events.
    ///
    /// To allow additional paths: add a prefix entry to the appropriate list.
    /// Segment-bounded matching is used: the path must match the prefix exactly
    /// up to a path separator or end of string, preventing prefix spoofing.
    /// Registry paths are expected without the hive prefix (e.g. "SOFTWARE\\Microsoft\\..." not "HKLM\\...").
    /// File paths should be expanded (no environment variables).
    /// </summary>
    public static class GatherRuleGuards
    {
        // -----------------------------------------------------------------------
        // Registry — subPath after hive prefix stripped (HKLM\\ / HKCU\\)
        // -----------------------------------------------------------------------
        public static readonly IReadOnlyList<string> AllowedRegistryPrefixes = new[]
        {
            // MDM / Enrollment
            @"SOFTWARE\Microsoft\Enrollments",
            @"SOFTWARE\Microsoft\EnterpriseDesktopAppManagement",
            @"SOFTWARE\Microsoft\Provisioning",
            @"SOFTWARE\Microsoft\PolicyManager",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\MDM",

            // AAD / Hybrid Join / Workplace Join
            @"SOFTWARE\Microsoft\IdentityStore",
            @"SYSTEM\CurrentControlSet\Control\CloudDomainJoin",

            // Windows Update / WUfB
            @"SOFTWARE\Microsoft\WindowsUpdate",
            @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate",

            // BitLocker
            @"SOFTWARE\Microsoft\BitLocker",
            @"SYSTEM\CurrentControlSet\Control\BitLockerStatus",

            // Network / Proxy (enrollment connectivity)
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings",
            @"SYSTEM\CurrentControlSet\Services\Tcpip",

            // Autopilot / OOBE / Setup
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Setup",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE",
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",

            // TPM
            @"SYSTEM\CurrentControlSet\Services\TPM",
            @"SOFTWARE\Microsoft\Tpm",

            // Intune IME
            @"SOFTWARE\Microsoft\IntuneManagementExtension",

            // SCEP / Certificate enrollment
            @"SOFTWARE\Microsoft\SystemCertificates",
            @"SOFTWARE\Policies\Microsoft\SystemCertificates",
        };

        // -----------------------------------------------------------------------
        // File — after Environment.ExpandEnvironmentVariables
        // -----------------------------------------------------------------------
        public static readonly IReadOnlyList<string> AllowedFilePrefixes = new[]
        {
            // Intune Management Extension logs
            @"C:\ProgramData\Microsoft\IntuneManagementExtension",

            // ConfigMgr / MECM client logs (hybrid scenarios)
            @"C:\Windows\CCM\Logs",

            // Windows setup / upgrade logs
            @"C:\Windows\Logs",
            @"C:\Windows\Panther",
            @"C:\Windows\SetupDiag",

            // MDM Diagnostics
            @"C:\ProgramData\Microsoft\DiagnosticLogCSP",

            // Windows Update logs
            @"C:\Windows\SoftwareDistribution\ReportingEvents.log",
        };

        // -----------------------------------------------------------------------
        // WMI — full query string must start with one of these prefixes
        // Only SELECT queries against known safe classes are permitted.
        // -----------------------------------------------------------------------
        public static readonly IReadOnlyList<string> AllowedWmiQueryPrefixes = new[]
        {
            // OS / Hardware identity
            "SELECT * FROM Win32_OperatingSystem",
            "SELECT * FROM Win32_ComputerSystem",
            "SELECT * FROM Win32_BIOS",
            "SELECT * FROM Win32_Processor",
            "SELECT * FROM Win32_BaseBoard",
            "SELECT * FROM Win32_Battery",

            // TPM
            "SELECT * FROM Win32_TPM",

            // Network
            "SELECT * FROM Win32_NetworkAdapter",
            "SELECT * FROM Win32_NetworkAdapterConfiguration",

            // Storage
            "SELECT * FROM Win32_DiskDrive",
            "SELECT * FROM Win32_LogicalDisk",

            // Windows licensing / activation
            "SELECT * FROM SoftwareLicensingProduct",
        };

        // -----------------------------------------------------------------------
        // Guard methods
        // -----------------------------------------------------------------------

        /// <summary>
        /// Returns true if the registry subPath (hive stripped) matches an allowed prefix
        /// with segment-bounded matching (next char must be '\' or end of string).
        /// </summary>
        public static bool IsRegistryPathAllowed(string subPath)
        {
            if (string.IsNullOrEmpty(subPath))
                return false;

            return AllowedRegistryPrefixes.Any(prefix =>
                subPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                (subPath.Length == prefix.Length || subPath[prefix.Length] == '\\'));
        }

        /// <summary>
        /// Returns true if the expanded file path matches an allowed prefix
        /// with segment-bounded matching and path normalization to prevent traversal.
        /// </summary>
        public static bool IsFilePathAllowed(string expandedPath)
        {
            if (string.IsNullOrEmpty(expandedPath))
                return false;

            string normalizedPath;
            try
            {
                normalizedPath = Path.GetFullPath(expandedPath);
            }
            catch
            {
                return false;
            }

            return AllowedFilePrefixes.Any(prefix =>
                normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                (normalizedPath.Length == prefix.Length || normalizedPath[prefix.Length] == '\\'));
        }

        /// <summary>
        /// Returns true if the WMI query (trimmed) matches an allowed prefix
        /// with boundary matching (next char must be whitespace or end of string).
        /// </summary>
        public static bool IsWmiQueryAllowed(string query)
        {
            if (string.IsNullOrEmpty(query))
                return false;

            var trimmed = query.Trim();
            return AllowedWmiQueryPrefixes.Any(prefix =>
                trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                (trimmed.Length == prefix.Length || char.IsWhiteSpace(trimmed[prefix.Length])));
        }
    }
}
