using System;
using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.Agent.Core.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.Core.Monitoring.Tracking
{
    /// <summary>
    /// Handles persistence of EnrollmentTracker state to disk (JSON serialization).
    /// Enables crash-recovery: after agent restart, completion signals already seen
    /// (ESP exit, desktop arrival, etc.) are restored so the enrollment can complete.
    /// Pattern follows ImeTrackerStatePersistence.
    /// </summary>
    public class EnrollmentStatePersistence
    {
        private readonly string _stateDirectory;
        private readonly string _stateFilePath;
        private readonly AgentLogger _logger;

        public EnrollmentStatePersistence(string stateDirectory, AgentLogger logger)
        {
            _stateDirectory = Environment.ExpandEnvironmentVariables(stateDirectory);
            _stateFilePath = Path.Combine(_stateDirectory, "enrollment-state.json");
            _logger = logger;
        }

        public string StateFilePath => _stateFilePath;

        /// <summary>
        /// Loads persisted state from disk. Returns null if no state file exists or on error.
        /// </summary>
        public EnrollmentStateData Load()
        {
            if (!File.Exists(_stateFilePath))
            {
                _logger.Info("EnrollmentStatePersistence: no persisted state found (fresh enrollment)");
                return null;
            }

            try
            {
                var json = File.ReadAllText(_stateFilePath);
                var state = JsonConvert.DeserializeObject<EnrollmentStateData>(json);
                if (state == null)
                {
                    _logger.Warning("EnrollmentStatePersistence: persisted state file was empty or invalid");
                    return null;
                }
                return state;
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentStatePersistence: failed to load persisted state, starting fresh: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Persists tracker state to disk as JSON (atomic write via temp file).
        /// </summary>
        public void Save(EnrollmentStateData state)
        {
            try
            {
                Directory.CreateDirectory(_stateDirectory);
                var tempPath = _stateFilePath + ".tmp";
                File.WriteAllText(tempPath, JsonConvert.SerializeObject(state, Formatting.Indented));
                File.Copy(tempPath, _stateFilePath, overwrite: true);
                try { File.Delete(tempPath); } catch { }
            }
            catch (Exception ex)
            {
                _logger.Debug($"EnrollmentStatePersistence: failed to save state: {ex.Message}");
            }
        }

        /// <summary>
        /// Deletes persisted state file.
        /// </summary>
        public void Delete()
        {
            try
            {
                if (File.Exists(_stateFilePath))
                {
                    File.Delete(_stateFilePath);
                    _logger.Info("EnrollmentStatePersistence: persisted state deleted");
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"EnrollmentStatePersistence: failed to delete state file: {ex.Message}");
            }
        }
    }

    // ---- DTO for state serialization ----

    public class EnrollmentStateData
    {
        public bool EspEverSeen { get; set; }
        public bool EspFinalExitSeen { get; set; }
        public bool DesktopArrived { get; set; }
        public string LastEspPhase { get; set; }
        public bool IsWaitingForHello { get; set; }
        public DateTime? WaitingForHelloStartedUtc { get; set; }
        public bool EnrollmentCompleteEmitted { get; set; }
        public string EnrollmentType { get; set; }

        // ESP configuration from registry (FirstSync\SkipUserStatusPage / SkipDeviceStatusPage)
        public bool? SkipUserStatusPage { get; set; }
        public bool? SkipDeviceStatusPage { get; set; }

        // Autopilot deployment mode: 0=UserDriven, 1=SelfDeploying, 2=PreProvisioning, null=unknown
        public int? AutopilotMode { get; set; }

        // True when AAD join status shows a joined device with a real user email
        public bool AadJoinedWithUser { get; set; }

        // True when Autopilot profile indicates Hybrid Azure AD Join (CloudAssignedDomainJoinMethod == 1)
        public bool IsHybridJoin { get; set; }

        // Signal timestamps for audit trail
        public DateTime? EspFirstSeenUtc { get; set; }
        public DateTime? EspFinalExitUtc { get; set; }
        public DateTime? DesktopArrivedUtc { get; set; }
        public DateTime? HelloResolvedUtc { get; set; }
        public DateTime? ImePatternSeenUtc { get; set; }
        public DateTime? DeviceSetupProvisioningCompleteUtc { get; set; }
        public List<string> SignalsSeen { get; set; } = new List<string>();

        // ESP provisioning settle wait state (crash recovery)
        public bool IsWaitingForEspSettle { get; set; }
        public DateTime? WaitingForEspSettleStartedUtc { get; set; }

        /// <summary>
        /// Explicit completion state machine state (dual-write).
        /// When present, used to restore the state machine directly.
        /// When absent (old format), the state is reconstructed from boolean flags.
        /// Serialized as string (enum name) for forward compatibility.
        /// </summary>
        public string CompletionState { get; set; }
    }
}
