using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AutopilotMonitor.Agent.Core.Monitoring.Collectors
{
    /// <summary>
    /// Privacy and security guards for configurable diagnostics log paths.
    ///
    /// Restricts user-defined diagnostics paths to directories that contain only
    /// enrollment- and device-management-relevant log files, preventing PII leakage.
    ///
    /// Rules:
    ///   - The path (after environment variable expansion and full-path normalization)
    ///     must start with one of the allowed prefixes.
    ///   - Segment-bounded matching: next character after the prefix must be '\' or end of string.
    ///   - Wildcards ('*', '?') are only permitted in the last path segment (filename part).
    ///   - Path traversal sequences are blocked via Path.GetFullPath normalization.
    /// </summary>
    public static class DiagnosticsPathGuards
    {
        // -----------------------------------------------------------------------
        // Allowed directory prefixes for diagnostics log paths
        // (expanded, absolute paths — no environment variables)
        // -----------------------------------------------------------------------
        public static readonly IReadOnlyList<string> AllowedDiagnosticsPathPrefixes = new[]
        {
            // Autopilot Monitor agent logs
            @"C:\ProgramData\AutopilotMonitor",

            // Intune Management Extension
            @"C:\ProgramData\Microsoft\IntuneManagementExtension",

            // Windows Setup & OOBE
            @"C:\Windows\Panther",

            // Windows general logs
            @"C:\Windows\Logs",

            // Windows Setup Diagnostics
            @"C:\Windows\SetupDiag",

            // Windows Update reporting
            @"C:\Windows\SoftwareDistribution\ReportingEvents.log",

            // Windows Event Log files
            @"C:\Windows\System32\winevt\Logs",

            // SCCM / ConfigMgr
            @"C:\Windows\CCM\Logs",

            // Microsoft Diagnostic Log CSP
            @"C:\ProgramData\Microsoft\DiagnosticLogCSP",

            // Windows Error Reporting
            @"C:\ProgramData\Microsoft\Windows\WER",

            // Windows Component-Based Servicing
            @"C:\Windows\Logs\CBS",
        };

        /// <summary>
        /// Returns true if the given path is allowed for diagnostics collection.
        /// Expands environment variables and normalises to a full path before checking.
        /// Wildcards in the last path segment are supported.
        /// </summary>
        public static bool IsDiagnosticsPathAllowed(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return false;

            try
            {
                // Split off a trailing wildcard segment before normalization
                var expanded = Environment.ExpandEnvironmentVariables(rawPath);
                var fileName = Path.GetFileName(expanded);
                var hasWildcard = fileName.Contains('*') || fileName.Contains('?');

                // Normalize the directory part (or full path if no wildcard)
                string normalizedDir;
                if (hasWildcard)
                {
                    var dir = Path.GetDirectoryName(expanded);
                    if (string.IsNullOrEmpty(dir))
                        return false;
                    normalizedDir = Path.GetFullPath(dir);
                }
                else
                {
                    normalizedDir = Path.GetFullPath(expanded);
                }

                foreach (var prefix in AllowedDiagnosticsPathPrefixes)
                {
                    var normalizedPrefix = Path.GetFullPath(prefix);
                    if (normalizedDir.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        // Segment-bounded: next char must be '\' or end-of-string
                        if (normalizedDir.Length == normalizedPrefix.Length ||
                            normalizedDir[normalizedPrefix.Length] == Path.DirectorySeparatorChar)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
            catch
            {
                // Any path normalization failure → deny
                return false;
            }
        }
    }
}
