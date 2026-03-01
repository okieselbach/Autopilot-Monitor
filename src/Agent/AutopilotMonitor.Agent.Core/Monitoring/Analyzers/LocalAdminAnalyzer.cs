using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.Core.Monitoring.Analyzers
{
    /// <summary>
    /// Analyzes local administrator accounts and user profiles on the device
    /// to detect pre-enrollment admin account creation — a known Autopilot bypass technique.
    ///
    /// Checks performed:
    ///   1. BypassNRO registry flag (HKLM\...\OOBE\BypassNRO = 1)
    ///   2. Unexpected local user accounts (via WMI Win32_UserAccount)
    ///   3. Unexpected C:\Users profile directories
    ///
    /// Confidence scoring:
    ///   BypassNRO = 1                          → +20 (low indicator)
    ///   Unexpected local account found         → +40 (medium indicator)
    ///   Account + matching C:\Users profile    → +40 (high indicator, profile overlap)
    ///
    /// Emits a single "local_admin_analysis" event at startup and at shutdown,
    /// enabling delta detection between pre- and post-enrollment state.
    /// </summary>
    public class LocalAdminAnalyzer : IAgentAnalyzer
    {
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly Action<EnrollmentEvent> _emitEvent;
        private readonly AgentLogger _logger;
        private readonly List<string> _allowedAccounts;

        // Built-in accounts and profile folders always present on a freshly imaged Windows device.
        // "Public", "Default", "Default User", "All Users" are folders/junctions in C:\Users, not user accounts.
        // "defaultuser0" is a temporary OOBE/Autopilot system account created during enrollment.
        private static readonly List<string> BuiltInAllowedAccounts = new List<string>
        {
            "Administrator",
            "Guest",
            "DefaultAccount",
            "WDAGUtilityAccount",
            "defaultuser0",    // Temporary OOBE/Autopilot system account, present during enrollment
            "Public",          // Profile folder (not a user account)
            "Default",         // Default user profile template
            "Default User",    // Symlink to Default in some Windows versions
            "All Users"        // Junction pointing to C:\ProgramData, always present
        };

        public string Name => "LocalAdminAnalyzer";

        public LocalAdminAnalyzer(
            string sessionId,
            string tenantId,
            Action<EnrollmentEvent> emitEvent,
            AgentLogger logger,
            List<string> allowedAccounts = null)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId  = tenantId  ?? throw new ArgumentNullException(nameof(tenantId));
            _emitEvent = emitEvent ?? throw new ArgumentNullException(nameof(emitEvent));
            _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));

            // Tenant-supplied accounts are additive (union with built-ins, not replacement)
            _allowedAccounts = new List<string>(BuiltInAllowedAccounts);
            if (allowedAccounts != null)
            {
                foreach (var account in allowedAccounts)
                {
                    if (!string.IsNullOrWhiteSpace(account) &&
                        !_allowedAccounts.Any(a => string.Equals(a, account, StringComparison.OrdinalIgnoreCase)))
                    {
                        _allowedAccounts.Add(account);
                    }
                }
            }
        }

        public void AnalyzeAtStartup()
        {
            _logger.Info($"{Name}: Running startup analysis");
            RunAnalysis("startup", EnrollmentPhase.Start);
        }

        public void AnalyzeAtShutdown()
        {
            _logger.Info($"{Name}: Running shutdown analysis");
            RunAnalysis("shutdown", EnrollmentPhase.Complete);
        }

        // -----------------------------------------------------------------------
        // Core analysis
        // -----------------------------------------------------------------------

        private void RunAnalysis(string trigger, EnrollmentPhase phase)
        {
            try
            {
                var bypassNroResult  = CheckBypassNroRegistry();
                var accountsResult   = CheckLocalAdminAccounts();
                var profilesResult   = CheckUserProfiles();

                int confidenceScore = 0;

                if (bypassNroResult.Value == 1)
                    confidenceScore += 20;

                if (accountsResult.Unexpected.Count > 0)
                    confidenceScore += 40;

                // Profile overlap: unexpected account AND matching C:\Users folder
                bool profileOverlap = accountsResult.Unexpected.Any(a =>
                    profilesResult.Unexpected.Any(p =>
                        string.Equals(a, p, StringComparison.OrdinalIgnoreCase)));
                if (profileOverlap)
                    confidenceScore += 40;

                confidenceScore = Math.Min(confidenceScore, 100);

                EventSeverity severity;
                string findingLabel;

                if (confidenceScore == 0)
                {
                    severity     = EventSeverity.Info;
                    findingLabel = "no_unexpected_admins_detected";
                }
                else if (confidenceScore < 40)
                {
                    severity     = EventSeverity.Info;
                    findingLabel = "bypass_nro_flag_only";
                }
                else if (confidenceScore < 80)
                {
                    severity     = EventSeverity.Warning;
                    findingLabel = "unexpected_local_admins_detected";
                }
                else
                {
                    severity     = EventSeverity.Error;
                    findingLabel = "unexpected_local_admins_detected";
                }

                _logger.Info(
                    $"{Name}: confidence={confidenceScore}, finding={findingLabel}, " +
                    $"bypassNro={bypassNroResult.Value}, " +
                    $"unexpectedAccounts={accountsResult.Unexpected.Count}, " +
                    $"unexpectedProfiles={profilesResult.Unexpected.Count}");

                var data = new Dictionary<string, object>
                {
                    { "confidence_score",           confidenceScore },
                    { "severity",                   severity.ToString().ToLower() },
                    { "finding",                    findingLabel },
                    { "triggered_at",               trigger },
                    { "enrollment_phase_at_check",  phase.ToString() },
                    { "allowed_accounts",           _allowedAccounts },
                    { "checks", new Dictionary<string, object>
                        {
                            { "bypass_nro", new Dictionary<string, object>
                                {
                                    { "value",   bypassNroResult.Value },
                                    { "flagged", bypassNroResult.Value == 1 }
                                }
                            },
                            { "unexpected_accounts",  accountsResult.Unexpected },
                            { "unexpected_profiles",  profilesResult.Unexpected },
                            { "accounts_checked",     accountsResult.AllChecked },
                            { "profiles_found",       profilesResult.AllFound }
                        }
                    }
                };

                var message = confidenceScore == 0
                    ? $"{Name}: No unexpected local admins detected"
                    : $"{Name}: Unexpected admin activity detected (confidence={confidenceScore})";

                _emitEvent(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId  = _tenantId,
                    EventType = "local_admin_analysis",
                    Severity  = severity,
                    Source    = Name,
                    Phase     = phase,
                    Message   = message,
                    Data      = data
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"{Name}: Analysis failed unexpectedly", ex);
            }
        }

        // -----------------------------------------------------------------------
        // Individual checks
        // -----------------------------------------------------------------------

        private BypassNroCheckResult CheckBypassNroRegistry()
        {
            try
            {
                const string keyPath   = @"SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE";
                const string valueName = "BypassNRO";

                using (var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: false))
                {
                    if (key == null)
                    {
                        _logger.Debug($"{Name}: BypassNRO registry key not found");
                        return new BypassNroCheckResult { Value = 0, KeyExists = false };
                    }

                    var raw = key.GetValue(valueName);
                    if (raw == null)
                    {
                        _logger.Debug($"{Name}: BypassNRO value not present");
                        return new BypassNroCheckResult { Value = 0, KeyExists = true };
                    }

                    var intValue = Convert.ToInt32(raw);
                    _logger.Debug($"{Name}: BypassNRO = {intValue}");
                    return new BypassNroCheckResult { Value = intValue, KeyExists = true };
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"{Name}: Failed to read BypassNRO registry: {ex.Message}");
                return new BypassNroCheckResult { Value = 0, KeyExists = false };
            }
        }

        private LocalAccountCheckResult CheckLocalAdminAccounts()
        {
            var allChecked = new List<string>();
            var unexpected = new List<string>();

            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name, Disabled FROM Win32_UserAccount WHERE LocalAccount = True"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var name     = obj["Name"]?.ToString() ?? string.Empty;
                        var disabled = obj["Disabled"] != null && Convert.ToBoolean(obj["Disabled"]);

                        if (string.IsNullOrEmpty(name))
                            continue;

                        allChecked.Add(name);

                        // Skip disabled accounts — they cannot be used to log in
                        if (disabled)
                            continue;

                        if (!_allowedAccounts.Any(a =>
                            string.Equals(a, name, StringComparison.OrdinalIgnoreCase)))
                        {
                            unexpected.Add(name);
                            _logger.Debug($"{Name}: Unexpected local account: {name}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"{Name}: Failed to enumerate local accounts via WMI: {ex.Message}");
            }

            return new LocalAccountCheckResult { AllChecked = allChecked, Unexpected = unexpected };
        }

        private UserProfileCheckResult CheckUserProfiles()
        {
            var allFound   = new List<string>();
            var unexpected = new List<string>();

            try
            {
                const string usersRoot = @"C:\Users";

                if (!Directory.Exists(usersRoot))
                {
                    _logger.Debug($"{Name}: C:\\Users does not exist");
                    return new UserProfileCheckResult { AllFound = allFound, Unexpected = unexpected };
                }

                var dirs = Directory.GetDirectories(usersRoot, "*", SearchOption.TopDirectoryOnly);
                foreach (var dir in dirs)
                {
                    var folderName = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(folderName))
                        continue;

                    allFound.Add(folderName);

                    if (!_allowedAccounts.Any(a =>
                        string.Equals(a, folderName, StringComparison.OrdinalIgnoreCase)))
                    {
                        unexpected.Add(folderName);
                        _logger.Debug($"{Name}: Unexpected profile folder: {folderName}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"{Name}: Failed to enumerate user profiles: {ex.Message}");
            }

            return new UserProfileCheckResult { AllFound = allFound, Unexpected = unexpected };
        }

        // -----------------------------------------------------------------------
        // Private result types
        // -----------------------------------------------------------------------

        private class BypassNroCheckResult
        {
            public int  Value     { get; set; }
            public bool KeyExists { get; set; }
        }

        private class LocalAccountCheckResult
        {
            public List<string> AllChecked { get; set; } = new List<string>();
            public List<string> Unexpected { get; set; } = new List<string>();
        }

        private class UserProfileCheckResult
        {
            public List<string> AllFound   { get; set; } = new List<string>();
            public List<string> Unexpected { get; set; } = new List<string>();
        }
    }
}
