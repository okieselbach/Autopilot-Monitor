using System;
using System.IO;
using System.Reflection;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Termination
{
    /// <summary>
    /// Launches <c>AutopilotMonitor.SummaryDialog.exe</c> in the signed-in user's session
    /// with a freshly-written <c>final-status.json</c>. Plan §4.x M4.6.β.
    /// <para>
    /// Sequence (Legacy parity):
    /// </para>
    /// <list type="number">
    ///   <item>Resolve the dialog exe next to the agent binary (<c>Assembly.Location</c>).</item>
    ///   <item>Write the final-status.json to the agent state directory.</item>
    ///   <item>Copy exe + dependencies + final-status.json to a per-launch temp directory under
    ///     <c>%ProgramData%\AutopilotMonitor-Summary\</c> (the dialog deletes itself via <c>--cleanup</c>).</item>
    ///   <item>Grant the signed-in user delete permission on that temp directory so the dialog's
    ///     self-cleanup step works (runs as user).</item>
    ///   <item>Invoke <see cref="UserSessionProcessLauncher.LaunchInUserSession"/> with the right args.</item>
    /// </list>
    /// <para>
    /// Fire-and-forget: the launcher does not wait for the dialog to exit. Returns <c>false</c>
    /// on any failure, never throws. Result is logged by the caller so a dialog failure never
    /// blocks the cleanup/shutdown path.
    /// </para>
    /// </summary>
    public static class SummaryDialogLauncher
    {
        internal const string DialogExeName = "AutopilotMonitor.SummaryDialog.exe";
        internal const string DialogConfigName = "AutopilotMonitor.SummaryDialog.exe.config";
        internal const string NewtonsoftDependency = "Newtonsoft.Json.dll";
        internal const string SummaryTempRootEnvVar = "AutopilotMonitor-Summary";
        internal const string FinalStatusFileName = "final-status.json";

        /// <summary>
        /// Writes <paramref name="status"/> to <paramref name="stateDirectory"/>/<c>final-status.json</c>
        /// and launches the dialog. When <see cref="AgentConfiguration.ShowEnrollmentSummary"/> is
        /// false, only the JSON is written (skipping the launch) — tests and backend-consumable
        /// audit trail benefit from always having the file on disk.
        /// </summary>
        /// <returns><c>true</c> when both write and launch (if enabled) succeeded.</returns>
        public static bool WriteAndLaunch(
            FinalStatus status,
            AgentConfiguration configuration,
            string stateDirectory,
            AgentLogger logger,
            string dialogExePathOverride = null)
        {
            if (status == null) throw new ArgumentNullException(nameof(status));
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (string.IsNullOrEmpty(stateDirectory)) throw new ArgumentException("stateDirectory required.", nameof(stateDirectory));
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            // 1) Write final-status.json always (cheap, useful for audit even if dialog skipped).
            string statusPath;
            try
            {
                Directory.CreateDirectory(stateDirectory);
                statusPath = Path.Combine(stateDirectory, FinalStatusFileName);
                File.WriteAllText(statusPath, JsonConvert.SerializeObject(status, Formatting.Indented));
                logger.Info($"SummaryDialogLauncher: final-status.json written to {statusPath}.");
            }
            catch (Exception ex)
            {
                logger.Warning($"SummaryDialogLauncher: final-status.json write failed: {ex.Message}");
                return false;
            }

            if (!configuration.ShowEnrollmentSummary)
            {
                logger.Info("SummaryDialogLauncher: ShowEnrollmentSummary=false — skipping dialog launch.");
                return true;
            }

            // 2) Resolve dialog exe.
            var dialogExe = dialogExePathOverride;
            if (string.IsNullOrEmpty(dialogExe))
            {
                var agentDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                dialogExe = Path.Combine(agentDir, DialogExeName);
            }

            if (!File.Exists(dialogExe))
            {
                logger.Warning($"SummaryDialogLauncher: dialog exe not found at '{dialogExe}' — skipping launch.");
                return false;
            }

            // 3) Copy to temp dir (dialog self-deletes via --cleanup).
            string tempDir;
            string tempExe;
            try
            {
                tempDir = Path.Combine(Path.GetTempPath(), SummaryTempRootEnvVar, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);

                tempExe = Path.Combine(tempDir, DialogExeName);
                File.Copy(dialogExe, tempExe, overwrite: true);

                var sourceDir = Path.GetDirectoryName(dialogExe) ?? string.Empty;
                TryCopy(Path.Combine(sourceDir, DialogConfigName), Path.Combine(tempDir, DialogConfigName));
                TryCopy(Path.Combine(sourceDir, NewtonsoftDependency), Path.Combine(tempDir, NewtonsoftDependency));

                var tempStatusFile = Path.Combine(tempDir, FinalStatusFileName);
                File.Copy(statusPath, tempStatusFile, overwrite: true);
            }
            catch (Exception ex)
            {
                logger.Warning($"SummaryDialogLauncher: temp dir setup failed: {ex.Message}");
                return false;
            }

            // 4) Compose args. Legacy contract: --status-file <path> --timeout <s> --cleanup [--branding-url <url>].
            var tempStatusArg = Path.Combine(tempDir, FinalStatusFileName);
            var args = $"--status-file \"{tempStatusArg}\" --timeout {configuration.EnrollmentSummaryTimeoutSeconds} --cleanup";
            if (!string.IsNullOrWhiteSpace(configuration.EnrollmentSummaryBrandingImageUrl))
                args += $" --branding-url \"{configuration.EnrollmentSummaryBrandingImageUrl}\"";

            // 5) Launch in user session with retry.
            var retrySeconds = configuration.EnrollmentSummaryLaunchRetrySeconds > 0
                ? configuration.EnrollmentSummaryLaunchRetrySeconds
                : 120;

            var launched = UserSessionProcessLauncher.LaunchInUserSession(tempExe, args, logger, retrySeconds);
            if (launched)
                logger.Info($"SummaryDialogLauncher: dialog launched (exe='{tempExe}', retrySeconds={retrySeconds}).");
            else
                logger.Warning("SummaryDialogLauncher: dialog launch failed or no user session available.");

            return launched;
        }

        private static void TryCopy(string source, string destination)
        {
            try { if (File.Exists(source)) File.Copy(source, destination, overwrite: true); }
            catch { /* best-effort — the dialog degrades gracefully on missing config/deps */ }
        }
    }
}
