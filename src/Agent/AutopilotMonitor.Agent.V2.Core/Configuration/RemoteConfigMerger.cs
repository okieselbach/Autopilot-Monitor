using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Configuration
{
    /// <summary>
    /// Projects the remote <see cref="AgentConfigResponse"/> onto the runtime
    /// <see cref="AgentConfiguration"/>. Before this merger existed the agent fetched the remote
    /// config and stored it internally in <see cref="RemoteConfigService.CurrentConfig"/>, but
    /// only <c>Collectors</c>, <c>ImeLogPatterns</c>, <c>GatherRules</c>,
    /// <c>WhiteGloveSealingPatternIds</c> and <c>LatestAgentSha256</c> were consumed downstream.
    /// All other tenant-controlled knobs — <c>SelfDestructOnComplete</c>, <c>KeepLogFile</c>,
    /// <c>NtpServer</c>, <c>DiagnosticsUploadMode</c>, <c>ShowEnrollmentSummary</c>, the reboot
    /// policy, the log level, the lifetime watchdog and the guardrail-relax switch — were bound
    /// to <see cref="AgentConfiguration"/> defaults set from CLI / bootstrap-config, so tenant
    /// admin settings were effectively ignored.
    /// <para>
    /// CLI-override semantics: command-line flags that were explicitly passed take precedence
    /// over the remote value. In production (Scheduled Task run with no args) the remote value
    /// always wins; in local dev runs the developer can still force a specific behaviour with
    /// a CLI flag without their tenant remote config clobbering it.
    /// </para>
    /// </summary>
    public static class RemoteConfigMerger
    {
        /// <summary>
        /// Overwrites the relevant <paramref name="agentConfig"/> fields with values from
        /// <paramref name="remoteConfig"/>. Unknown / null / empty remote fields keep the
        /// current <paramref name="agentConfig"/> value (partial-remote-config tolerance).
        /// </summary>
        public static void Merge(
            AgentConfiguration agentConfig,
            AgentConfigResponse remoteConfig,
            string[] cliArgs)
        {
            if (agentConfig == null) throw new ArgumentNullException(nameof(agentConfig));
            if (remoteConfig == null) return;
            cliArgs = cliArgs ?? Array.Empty<string>();

            // ---- Boolean flags that have a matching CLI override — CLI wins when present.
            if (!ContainsFlag(cliArgs, "--no-cleanup"))
                agentConfig.SelfDestructOnComplete = remoteConfig.SelfDestructOnComplete;
            if (!ContainsFlag(cliArgs, "--keep-logfile"))
                agentConfig.KeepLogFile = remoteConfig.KeepLogFile;
            if (!ContainsFlag(cliArgs, "--reboot-on-complete"))
                agentConfig.RebootOnComplete = remoteConfig.RebootOnComplete;
            if (!ContainsFlag(cliArgs, "--disable-geolocation"))
                agentConfig.EnableGeoLocation = remoteConfig.EnableGeoLocation;

            // ---- Log level — CLI `--log-level <value>` wins. Remote is a string that may not parse.
            if (!ContainsValueArg(cliArgs, "--log-level")
                && !string.IsNullOrWhiteSpace(remoteConfig.LogLevel)
                && Enum.TryParse<AgentLogLevel>(remoteConfig.LogLevel, ignoreCase: true, out var parsedLevel))
            {
                agentConfig.LogLevel = parsedLevel;
            }

            // ---- Fields without a CLI override — always apply remote.
            agentConfig.RebootDelaySeconds = remoteConfig.RebootDelaySeconds;
            agentConfig.ShowEnrollmentSummary = remoteConfig.ShowEnrollmentSummary;
            agentConfig.EnrollmentSummaryTimeoutSeconds = remoteConfig.EnrollmentSummaryTimeoutSeconds;
            agentConfig.EnrollmentSummaryBrandingImageUrl = remoteConfig.EnrollmentSummaryBrandingImageUrl;
            agentConfig.EnrollmentSummaryLaunchRetrySeconds = remoteConfig.EnrollmentSummaryLaunchRetrySeconds;

            if (!string.IsNullOrWhiteSpace(remoteConfig.NtpServer))
                agentConfig.NtpServer = remoteConfig.NtpServer;
            agentConfig.EnableTimezoneAutoSet = remoteConfig.EnableTimezoneAutoSet;

            agentConfig.DiagnosticsUploadEnabled = remoteConfig.DiagnosticsUploadEnabled;
            if (!string.IsNullOrWhiteSpace(remoteConfig.DiagnosticsUploadMode))
                agentConfig.DiagnosticsUploadMode = remoteConfig.DiagnosticsUploadMode;
            agentConfig.DiagnosticsLogPaths = remoteConfig.DiagnosticsLogPaths ?? new List<DiagnosticsLogPath>();

            agentConfig.SendTraceEvents = remoteConfig.SendTraceEvents;
            agentConfig.UnrestrictedMode = remoteConfig.UnrestrictedMode;

            // ---- Positive-only int knobs — a zero or negative remote value keeps the current default.
            if (remoteConfig.UploadIntervalSeconds > 0)
                agentConfig.UploadIntervalSeconds = remoteConfig.UploadIntervalSeconds;
            if (remoteConfig.MaxBatchSize > 0)
                agentConfig.MaxBatchSize = remoteConfig.MaxBatchSize;

            // ---- Nested: CollectorConfiguration. Only the fields that have a V2 consumer
            //      on AgentConfiguration are projected here; the rest flow directly from
            //      remoteConfig.Collectors via DefaultComponentFactory.
            var collectors = remoteConfig.Collectors;
            if (collectors != null)
            {
                // AgentMaxLifetimeMinutes: 0 = disabled (valid); passed to the orchestrator as a TimeSpan?.
                agentConfig.AgentMaxLifetimeMinutes = collectors.AgentMaxLifetimeMinutes;
                if (collectors.HelloWaitTimeoutSeconds > 0)
                    agentConfig.HelloWaitTimeoutSeconds = collectors.HelloWaitTimeoutSeconds;
            }
        }

        private static bool ContainsFlag(string[] args, string flag)
        {
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static bool ContainsValueArg(string[] args, string flag)
        {
            // Value-args need both the flag and a subsequent token, otherwise the CLI helper would
            // not have parsed them — mirror GetArgValue's "i < args.Length - 1" contract.
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}
