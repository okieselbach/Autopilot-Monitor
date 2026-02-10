using System.Collections.Generic;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Built-in IME log regex patterns for smart enrollment tracking.
    /// These patterns are adapted from the EspOverlay project's proven IME log parsing logic.
    /// Delivered to the agent via the config API so patterns can be updated without agent rebuild.
    ///
    /// Pattern uses {GUID} as placeholder which the agent expands to the standard GUID capture regex.
    /// Named capture groups (e.g., (?&lt;id&gt;...)) are passed to the action handler.
    /// </summary>
    public static class BuiltInImeLogPatterns
    {
        public static List<ImeLogPattern> GetAll()
        {
            return new List<ImeLogPattern>
            {
                // ============================================================
                // CATEGORY: always (active regardless of ESP phase)
                // ============================================================

                new ImeLogPattern
                {
                    PatternId = "IME-STARTED",
                    Category = "always",
                    Pattern = @"EMS Agent Started",
                    Action = "imeStarted"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-SESSION-CHANGE",
                    Category = "always",
                    Pattern = @"OnSessionChange:? (?<change>.*?)$",
                    Action = "imeSessionChange"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-ESP-PHASE",
                    Category = "always",
                    Pattern = @"\[Win32App\] (?:In|The) EspPhase: (?<espPhase>\w+)",
                    Action = "espPhaseDetected"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-TOKEN-SUCCESS",
                    Category = "always",
                    Pattern = @"Successfully get the token",
                    Action = "espPhaseDetected",
                    Parameters = new Dictionary<string, string> { { "phase", "AccountSetup" } }
                },
                new ImeLogPattern
                {
                    PatternId = "IME-SET-CURRENT-1",
                    Category = "always",
                    Pattern = @"\[Win32App\] ProcessAppWithDependencies starts for {GUID} with name",
                    Action = "setCurrentApp"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-SET-CURRENT-2",
                    Category = "always",
                    Pattern = @"\[Win32App\] ExecManager: processing targeted app.*?\bid\W+{GUID}",
                    Action = "setCurrentApp"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-SET-CURRENT-3",
                    Category = "always",
                    Pattern = @"\[Win32App\] ===Step=== Start to Present app {GUID}",
                    Action = "setCurrentApp"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-SET-CURRENT-4",
                    Category = "always",
                    Pattern = @"\[Win32App\] SetCurrentDirectory:.*?\\{GUID}_\d+",
                    Action = "setCurrentApp"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-AGENT-VERSION",
                    Category = "always",
                    Pattern = @"Agent version is: (?<agentVersion>[\d.]+)",
                    Action = "imeAgentVersion"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-IMPERSONATION",
                    Category = "always",
                    Pattern = @"After impersonation: (?<user>.*?)$",
                    Action = "imeImpersonation"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-USER-SESSION-COMPLETED",
                    Category = "always",
                    Pattern = @"\[Win32App\] \.+ Completed user session \d+, userId: (?<userId>[a-f0-9\-]+), userSID: (?<userSid>S-[\d\-]+) \.+",
                    Action = "enrollmentCompleted"
                },

                // ============================================================
                // CATEGORY: currentPhase (active during current ESP phase)
                // ============================================================

                // --- Shutdown / Postpone ---
                new ImeLogPattern
                {
                    PatternId = "IME-SHUTDOWN",
                    Category = "currentPhase",
                    Pattern = @"EMS Agent received shutdown signal",
                    Action = "updateStatePostponed",
                    Parameters = new Dictionary<string, string> { { "useCurrentApp", "true" } }
                },

                // --- ESP Track Status (new logs) ---
                new ImeLogPattern
                {
                    PatternId = "IME-ESP-TRACK-STATUS",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\]\[EspManager\] Updating ESP tracked install status from (?<from>\w+) to (?<to>\w+) for application {GUID}\.",
                    Action = "espTrackStatus"
                },

                // --- Skipped / Not Applicable ---
                new ImeLogPattern
                {
                    PatternId = "IME-NO-ACTION",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\]\[ActionProcessor\] No action required for app with id: {GUID}",
                    Action = "updateStateSkipped"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-NOT-APPLICABLE-1",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\]\[ApplicabilityActionHandler\] Applicability check for policy with id: {GUID} resulted in action status: Success and applicability state: NotApplicable",
                    Action = "updateStateSkipped"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-NOT-APPLICABLE-2",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\]\[ReportingManager\] Applicability state for app with id: {GUID} has been updated\. Report delta: \{""ApplicabilityState"":\{""OldValue"":null,""NewValue"":""ScriptRequirementRuleNotMet""",
                    Action = "updateStateSkipped"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-NOT-APPLICABLE-3",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\]\[ReportingManager\] Desired state for app with id: {GUID} has been updated\. Report delta: \{""DesiredState"":\{""OldValue"":null,""NewValue"":""None""",
                    Action = "updateStateSkipped"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-NOT-APPLICABLE-4",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\]\[ReportingManager\] Desired state for app with id: {GUID} has been updated\. Report delta: \{""DesiredState"":\{""OldValue"":null,""NewValue"":""NotPresent""",
                    Action = "updateStateSkipped"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-NOT-TARGETED-1",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\]\[ActionProcessor\] App with id: {GUID}, targeted intent: RequiredInstall, and enforceability: Enforceable has projected enforcement classification: NotApplicableOrNotTargeted with desired state: None\.",
                    Action = "updateStateSkipped"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-NOT-TARGETED-2",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\]\[ActionProcessor\] App with id: {GUID}, effective intent: RequiredInstall, and enforceability: Enforceable has projected enforcement classification: NotApplicableOrNotTargeted with desired state: None\.",
                    Action = "updateStateSkipped"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-SKIP-FILTERS",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\]\[V3Processor\] All apps in the subgraph are not applicable due to assignment filters\. Skipping processing\.",
                    Action = "updateStateSkipped",
                    Parameters = new Dictionary<string, string> { { "useCurrentApp", "true" } }
                },

                // --- Old log format: Skipped patterns ---
                new ImeLogPattern
                {
                    PatternId = "IME-SKIP-UNINSTALL-NOTDETECTED",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\] Not detected app {GUID} and intent is to un-install, skip applicability check",
                    Action = "updateStateSkipped"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-SKIP-INSTALL-DETECTED",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\] Detected app {GUID} and intent is to install, skip applicability check",
                    Action = "updateStateSkipped"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-SKIP-APPLICABILITY-NOTMET",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\] (?=.*?\bapplicability\b)(?=.*?NotMet\b).*?\bapp\W+{GUID}\b",
                    Action = "updateStateSkipped"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-SKIP-UNINSTALL-NOTDETECTED-2",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\] NOT detected app {GUID}.*?skip un-installation",
                    Action = "updateStateSkipped"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-SKIP-DEPENDENCY-DETECT",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\] app\W+{GUID}\b.*?\bis a dependency\b.*?\bDetect Only mode\b.*?\bfinished processing",
                    Action = "updateStateSkipped"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-SKIP-DETECTED",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\] Detected app {GUID}.*?skip next step",
                    Action = "updateStateSkipped"
                },

                // --- Installed / Detected ---
                new ImeLogPattern
                {
                    PatternId = "IME-DETECTED-INSTALLED-1",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\]\[ReportingManager\] Detection state for app with id: {GUID} has been updated\. Report delta: \{""DetectionState"":\{""OldValue"":null,""NewValue"":""Installed""",
                    Action = "updateStateInstalled"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-DETECTED-INSTALLED-2",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\]\[DetectionActionHandler\] Detection for policy with id: {GUID} resulted in action status: Success and detection state: Detected",
                    Action = "updateStateInstalled"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-DETECTED-OLD",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\] Detected app {GUID} ",
                    Action = "updateStateInstalled"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-ENFORCEMENT-SUCCESS",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\]\[ReportingManager\] Execution state for app with id: {GUID} has been updated\. Report delta: \{""EnforcementState"":\{""OldValue"":""InProgressDownloadCompleted"",""NewValue"":""Success""",
                    Action = "updateStateInstalled"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-ENFORCEMENT-SUCCESS-OLD",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\] Setting enforcementState as\W+Success\b",
                    Action = "updateStateInstalled",
                    Parameters = new Dictionary<string, string> { { "useCurrentApp", "true" } }
                },

                // --- Installing ---
                new ImeLogPattern
                {
                    PatternId = "IME-INSTALL-HANDLER",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\]\[ExecutionActionHandler\] Handler invoked with execution type: Install for policy with id: {GUID}",
                    Action = "updateStateInstalling"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-LAUNCH-INSTALLER",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\] Launch Win32AppInstaller in \w+? session",
                    Action = "updateStateInstalling",
                    Parameters = new Dictionary<string, string> { { "useCurrentApp", "true" } }
                },
                new ImeLogPattern
                {
                    PatternId = "IME-WINGET-INSTALLING-1",
                    Category = "currentPhase",
                    Pattern = @"\[WinGetMessageProcessor\] Processing Progress for app = {GUID} and user = .*?- Operation Phase = Installing",
                    Action = "updateStateInstalling"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-WINGET-INSTALLING-2",
                    Category = "currentPhase",
                    Pattern = @"\[WinGetMessageProcessor\] Processing Progress for app = {GUID}.*?- Operation Phase = Installing",
                    Action = "updateStateInstalling"
                },

                // --- Downloading ---
                new ImeLogPattern
                {
                    PatternId = "IME-DOWNLOADING",
                    Category = "currentPhase",
                    Pattern = @"\[StatusService\] Downloading app \(id = {GUID}.*?\) via (?<tech>\w+), bytes (?<bytes>\w+)/(?<ofbytes>\w+) for user",
                    Action = "updateStateDownloading"
                },

                // --- Policies discovered ---
                new ImeLogPattern
                {
                    PatternId = "IME-POLICIES",
                    Category = "currentPhase",
                    Pattern = @"Get policies =\s*(?<policies>.*)",
                    Action = "policiesDiscovered"
                },

                // --- Update name (old log format) ---
                new ImeLogPattern
                {
                    PatternId = "IME-UPDATE-NAME-1",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\] ExecManager: processing targeted app.*?name\W+(?<name>.*?)\W+id\W+{GUID}",
                    Action = "updateName"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-UPDATE-NAME-2",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\] ProcessAppWithDependencies starts for {GUID} with name (?<name>.*?)$",
                    Action = "updateName"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-UPDATE-NAME-3",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\] Processing app \(id={GUID}, name = (?<name>.*?)\)",
                    Action = "updateName"
                },

                // --- Win32AppState (old log format) ---
                new ImeLogPattern
                {
                    PatternId = "IME-WIN32-STATE-1",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\] Got InstanceID: Win32App_{GUID}_\d+, installationStateString: (?<state>\d)",
                    Action = "updateWin32AppState"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-WIN32-STATE-2",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\] (?:Got|Set) (?:ESP )?win32App state (?<state>\w+) for {GUID}",
                    Action = "updateWin32AppState"
                },

                // --- Error states ---
                new ImeLogPattern
                {
                    PatternId = "IME-DECRYPT-FAILED",
                    Category = "currentPhase",
                    Pattern = @"Decryption is failed for appId {GUID}",
                    Action = "updateStateError"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-ERROR-DOWNLOAD",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\]\[ReportingManager\] Download state for app with id: {GUID} has been updated\. Report delta: \{""EnforcementState"":\{""OldValue"":""InProgress"",""NewValue"":""ErrorDownloadingContent""",
                    Action = "updateStateError"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-ERROR-UNMAPPED-EXIT",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\] Admin did NOT set mapping for lpExitCode\W+\d+ of app\W+{GUID}",
                    Action = "updateStateError"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-ERROR-ENFORCEMENT",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\] Setting enforcementState as\W+Error\b",
                    Action = "updateStateError",
                    Parameters = new Dictionary<string, string> { { "useCurrentApp", "true" } }
                },
                new ImeLogPattern
                {
                    PatternId = "IME-ERROR-TIMEOUT",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\] Installation is timeout",
                    Action = "updateStateError",
                    Parameters = new Dictionary<string, string> { { "useCurrentApp", "true" } }
                },
                new ImeLogPattern
                {
                    PatternId = "IME-ERROR-NO-CONTENT",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\] Didn't get content info from service",
                    Action = "updateStateError",
                    Parameters = new Dictionary<string, string> { { "useCurrentApp", "true" } }
                },
                new ImeLogPattern
                {
                    PatternId = "IME-ERROR-REPORT",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\]\[ReportingManager\] Execution state for app with id: {GUID} has been updated\. Report delta: \{""EnforcementState"":\{""OldValue"":""(?<from>\w+)"",""NewValue"":""(?<to>\w+)""",
                    Action = "updateStateError",
                    Parameters = new Dictionary<string, string> { { "checkTo", "true" } }
                },

                // --- DO Timeout / Postpone ---
                new ImeLogPattern
                {
                    PatternId = "IME-DO-TIMEOUT-1",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App DO\] DO download(?=.*?\bnot finished\b)(?=.*?\btimeout\b).*?intunewin-bin_{GUID}",
                    Action = "updateStatePostponed"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-DO-TIMEOUT-2",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App DO\] DO download(?=.*?\bnot finished\b)(?=.*?\btimeout\b)",
                    Action = "updateStatePostponed",
                    Parameters = new Dictionary<string, string> { { "useCurrentApp", "true" } }
                },

                // --- Subgraph processing (cancel stuck + set current) ---
                new ImeLogPattern
                {
                    PatternId = "IME-SUBGRAPH",
                    Category = "currentPhase",
                    Pattern = @"\[Win32App\]\[V3Processor\] Processing subgraph with app ids: {GUID}",
                    Action = "cancelStuckAndSetCurrent"
                },

                // ============================================================
                // CATEGORY: otherPhases (for apps completed in previous phases)
                // ============================================================

                new ImeLogPattern
                {
                    PatternId = "IME-ALREADY-DETECTED-1",
                    Category = "otherPhases",
                    Pattern = @"\[Win32App\] Completed detectionManager SideCar\w+?DetectionManager, applicationDetectedByCurrentRule: True",
                    Action = "ignoreCompletedApp"
                },
                new ImeLogPattern
                {
                    PatternId = "IME-ALREADY-DETECTED-2",
                    Category = "otherPhases",
                    Pattern = @"\[Win32App\] Setting enforcementState as\W+Success\b",
                    Action = "ignoreCompletedApp"
                }
            };
        }
    }
}
