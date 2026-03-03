using System;
using System.Diagnostics;
using System.Management;
using System.Threading;
using AutopilotMonitor.Agent.Core.Logging;

namespace AutopilotMonitor.Agent.Core.Monitoring.Collectors
{
    /// <summary>
    /// Detects when a real user desktop becomes available (explorer.exe under a non-system user).
    /// Fires DesktopArrived event exactly once per agent lifetime.
    /// Used for enrollment completion in no-ESP scenarios (WDP v2, ESP disabled) and
    /// as a backup signal for AccountSetup phase correction.
    /// </summary>
    public class DesktopArrivalDetector : IDisposable
    {
        private readonly AgentLogger _logger;
        private Timer _pollingTimer;
        private bool _desktopArrived;
        private const int PollingIntervalSeconds = 30;

        /// <summary>
        /// System/service account names that should NOT be considered real users.
        /// </summary>
        private static readonly string[] ExcludedUserNames =
        {
            "SYSTEM",
            "LOCAL SERVICE",
            "NETWORK SERVICE",
            "DefaultUser0",
            "DefaultUser1",
            "defaultuser0",
            "defaultuser1"
        };

        /// <summary>
        /// Fired exactly once when a real user desktop is detected (explorer.exe under a real user).
        /// </summary>
        public event EventHandler DesktopArrived;

        public DesktopArrivalDetector(AgentLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Start()
        {
            _logger.Info("DesktopArrivalDetector: starting (polling every 30s)");
            _pollingTimer = new Timer(
                PollForDesktop,
                null,
                TimeSpan.FromSeconds(5), // Initial check after 5s
                TimeSpan.FromSeconds(PollingIntervalSeconds));
        }

        public void Stop()
        {
            _pollingTimer?.Dispose();
            _pollingTimer = null;
        }

        private void PollForDesktop(object state)
        {
            if (_desktopArrived)
                return;

            try
            {
                var explorerProcesses = Process.GetProcessesByName("explorer");
                foreach (var proc in explorerProcesses)
                {
                    try
                    {
                        // Session 0 = SYSTEM session, skip
                        if (proc.SessionId == 0)
                            continue;

                        var owner = GetProcessOwner(proc.Id);
                        if (owner == null)
                            continue;

                        // Check against exclusion list
                        if (IsExcludedUser(owner))
                        {
                            _logger.Debug($"DesktopArrivalDetector: explorer.exe PID {proc.Id} owned by excluded user '{owner}' — skipping");
                            continue;
                        }

                        // Real user desktop detected
                        _desktopArrived = true;
                        _logger.Info($"DesktopArrivalDetector: real user desktop detected (explorer.exe PID {proc.Id}, session {proc.SessionId}, user '{owner}')");

                        // Stop polling
                        _pollingTimer?.Dispose();
                        _pollingTimer = null;

                        try { DesktopArrived?.Invoke(this, EventArgs.Empty); } catch { }
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug($"DesktopArrivalDetector: error checking explorer.exe PID {proc.Id}: {ex.Message}");
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"DesktopArrivalDetector: polling error: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the owner of a process using WMI (Win32_Process.GetOwner).
        /// Returns "DOMAIN\User" or "User" string, or null on failure.
        /// </summary>
        private string GetProcessOwner(int processId)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    $"SELECT * FROM Win32_Process WHERE ProcessId = {processId}"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var outParams = new object[2];
                        var result = (uint)obj.InvokeMethod("GetOwner", outParams);
                        if (result == 0)
                        {
                            var user = outParams[0]?.ToString();
                            var domain = outParams[1]?.ToString();
                            return string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"DesktopArrivalDetector: WMI GetOwner failed for PID {processId}: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Returns true if the user name matches any excluded system/service account.
        /// Handles both "User" and "DOMAIN\User" formats.
        /// Also matches the pattern DefaultUser* (case-insensitive).
        /// </summary>
        private static bool IsExcludedUser(string fullUserName)
        {
            if (string.IsNullOrEmpty(fullUserName))
                return true;

            // Extract just the username part (after backslash if present)
            var userName = fullUserName;
            var backslashIndex = fullUserName.LastIndexOf('\\');
            if (backslashIndex >= 0 && backslashIndex < fullUserName.Length - 1)
                userName = fullUserName.Substring(backslashIndex + 1);

            // Check exact matches
            foreach (var excluded in ExcludedUserNames)
            {
                if (string.Equals(userName, excluded, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Check DefaultUser* pattern
            if (userName.StartsWith("DefaultUser", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
