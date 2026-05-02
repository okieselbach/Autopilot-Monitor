using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
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
        private bool _excludedUserTraced; // Emit trace event only once for excluded-user skips
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

        /// <summary>
        /// Optional callback for trace events (decision, reason, context).
        /// Wired by MonitoringService to emit agent_trace events to the backend.
        /// </summary>
        public Action<string, string, Dictionary<string, object>> OnTraceEvent { get; set; }

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

        /// <summary>
        /// Resets desktop-arrival tracking after a placeholder→real-user transition (Hybrid
        /// User-Driven completion-gap fix, 2026-05-01). Used by the composition root when
        /// <see cref="AadJoinWatcher.AadUserJoined"/> fires for a real user — the previous
        /// fooUser desktop the detector observed is invalidated, and polling restarts so the
        /// AD-user desktop after the Hybrid reboot is detected as the actual real desktop.
        /// <para>
        /// Idempotent: safe to call multiple times. No-op if the detector already fired
        /// after the reset (a subsequent real-user join after a real-user desktop is also
        /// idempotent because the polling timer is restarted regardless and the next match
        /// against IsExcludedUser will short-circuit on the still-valid desktop).
        /// </para>
        /// </summary>
        public void ResetForRealUserSwitch()
        {
            _logger.Info("DesktopArrivalDetector: reset for real-user switch (placeholder→real user transition)");

            // Drop any prior arrival state — subsequent polls must re-evaluate the current
            // explorer.exe owner against IsExcludedUser anew.
            _desktopArrived = false;
            _excludedUserTraced = false;

            // Restart polling. Dispose any existing timer first to avoid leaking ticks
            // from an already-running schedule.
            _pollingTimer?.Dispose();
            _pollingTimer = new Timer(
                PollForDesktop,
                null,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(PollingIntervalSeconds));
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

                            // Trace this decision once so it's visible in the backend
                            if (!_excludedUserTraced)
                            {
                                _excludedUserTraced = true;
                                try
                                {
                                    OnTraceEvent?.Invoke(
                                        "desktop_excluded_user",
                                        $"explorer.exe found but owned by excluded user '{owner}' — not a real user desktop",
                                        new Dictionary<string, object> { { "pid", proc.Id }, { "session", proc.SessionId }, { "owner", owner } });
                                }
                                catch (Exception ex) { _logger.Verbose($"DesktopArrivalDetector: OnTraceEvent failed: {ex.Message}"); }
                            }
                            continue;
                        }

                        // Real user desktop detected
                        _desktopArrived = true;
                        _logger.Info($"DesktopArrivalDetector: real user desktop detected (explorer.exe PID {proc.Id}, session {proc.SessionId}, user '{owner}')");

                        try
                        {
                            OnTraceEvent?.Invoke(
                                "desktop_real_user_detected",
                                $"Real user desktop detected (explorer.exe PID {proc.Id}, user '[redacted]')",
                                new Dictionary<string, object> { { "pid", proc.Id }, { "session", proc.SessionId }, { "owner", "[redacted]" } });
                        }
                        catch (Exception ex) { _logger.Verbose($"DesktopArrivalDetector: OnTraceEvent failed: {ex.Message}"); }

                        // Stop polling
                        _pollingTimer?.Dispose();
                        _pollingTimer = null;

                        try { DesktopArrived?.Invoke(this, EventArgs.Empty); }
                        catch (Exception ex) { _logger.Warning($"DesktopArrivalDetector: DesktopArrived handler failed: {ex.Message}"); }
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
        /// Handles "User", "DOMAIN\User", and UPN ("user@domain") formats.
        /// Also matches the patterns DefaultUser* and the Autopilot provisioning placeholders
        /// foouser@* / autopilot@* (case-insensitive). The placeholder match prevents the
        /// fooUser OOBE shell on Hybrid User-Driven enrollments from being treated as a
        /// real user desktop.
        /// </summary>
        internal static bool IsExcludedUser(string fullUserName)
        {
            if (string.IsNullOrEmpty(fullUserName))
                return true;

            // UPN form (user@domain) — delegate to the same placeholder oracle the
            // AadJoinWatcher uses, so the foouser@/autopilot@ list stays in one place.
            if (fullUserName.IndexOf('@') >= 0
                && AadJoinInfo.IsPlaceholderUserEmail(fullUserName))
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

            // DOMAIN\foouser / DOMAIN\autopilot — the WMI GetOwner call sometimes returns a
            // synthetic local-machine domain instead of the UPN. EXACT match the bare
            // username here (not prefix), so legitimate real accounts like
            // CONTOSO\autopilotadmin or DOMAIN\foouserservice stay through the gate.
            // Codex review 2026-05-01 (Finding 3): the previous prefix match was too broad
            // and reused via UserProfileResolver, which would have resolved to the wrong
            // user profile for any account starting with "autopilot" or "foouser".
            // The UPN form (foouser@*, autopilot@*) is still handled above by
            // AadJoinInfo.IsPlaceholderUserEmail — that path is what covers the
            // real-world Autopilot placeholders (foouser@<tenant>.onmicrosoft.com).
            if (string.Equals(userName, "foouser", StringComparison.OrdinalIgnoreCase)
                || string.Equals(userName, "autopilot", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
