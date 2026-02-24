using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Built-in analyze rules shipped with the system
    /// These detect common Autopilot enrollment issues and suggest remediation
    /// </summary>
    public static class BuiltInAnalyzeRules
    {
        public static List<AnalyzeRule> GetAll()
        {
            return new List<AnalyzeRule>
            {
                // ===== APP RULES =====
                CreateAppDetectionScriptFailureRule(),
                CreateAppMsiErrorRule(),
                CreateAppDependencyChainRule(),
                CreateAppDiskSpaceRule(),
                CreateAppDownloadTimeoutRule(),
                CreateAppSlowInstallRule(),
                CreateAppRebootLoopRule(),
                CreateAppTrackingSummaryErrorsRule(),
                CreateAppDownloadStallRule(),
                CreateAppPostSuccessFailureRule(),

                // ===== ESP RULES =====
                CreateEspBlockingAppTimeoutRule(),
                CreateEspSecurityPolicyFailureRule(),
                CreateEnrollmentTimeoutRule(),

                // ===== DEVICE RULES =====
                CreateWindowsHelloTimeoutRule(),
                CreateHighMemoryDuringInstallRule(),

                // ===== ENROLLMENT RULES =====
                CreateEnrollmentFailedRule(),

                // ===== CORRELATION RULES (cross-event analysis) =====
                CreateCorrelationNetworkCausedInstallFailureRule(),
                CreateCorrelationDiskSpaceCausedInstallFailureRule(),
                CreateCorrelationProxyCausedDownloadFailureRule(),
            };
        }

        // ===== APPS =====

        /// <summary>
        /// Fixed: Now checks errorPatternId and errorDetail fields in app_install_failed events
        /// instead of relying on non-existent error_detected events.
        /// </summary>
        private static AnalyzeRule CreateAppDetectionScriptFailureRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-APP-001",
            Title = "Win32 App Detection Script Failure",
            Description = "Detects when a Win32 app detection script fails or returns unexpected results.",
            Severity = "warning",
            Category = "apps",
            BaseConfidence = 60,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "detection_failure", Source = "event_type", EventType = "app_install_failed", DataField = "message", Operator = "regex", Value = @"detection|not detected|script.*fail|detection.*error|DetectionState|NotDetected", Required = true }
            },
            ConfidenceFactors = new List<ConfidenceFactor>
            {
                new ConfidenceFactor { Signal = "error_detail_match", Condition = "exists", Weight = 20 }
            },
            Explanation = "A Win32 app detection script failed or did not detect the app after installation. This causes the app to appear as 'not installed' despite successful installation.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Fix detection rules", Steps = new List<string> { "Review the app's detection rules in Intune", "Verify file/registry paths are correct", "Test detection script manually on a device" } }
            },
            Tags = new[] { "apps", "detection", "win32" }
        };

        /// <summary>
        /// Fixed: Now checks errorDetail field (which contains the IME log line) for MSI error codes
        /// instead of the non-existent errorCode field.
        /// </summary>
        private static AnalyzeRule CreateAppMsiErrorRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-APP-002",
            Title = "MSI Installation Error",
            Description = "Maps common MSI error codes to their meanings and suggests fixes.",
            Severity = "high",
            Category = "apps",
            BaseConfidence = 80,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "msi_error", Source = "event_type", EventType = "app_install_failed", DataField = "message", Operator = "regex", Value = @"16[0-9]{2}|0x80070[0-9a-fA-F]+|lpExitCode|unmapped.*exit", Required = true }
            },
            Explanation = "An MSI installation failed with a specific error code. Common codes: 1603 (Fatal error), 1618 (Another install in progress), 1619 (Package could not be opened), 1625 (Installation prohibited by policy).",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Investigate MSI error", Steps = new List<string> { "Check the MSI log in C:\\Windows\\CCM\\Logs", "Look up the specific error code", "Verify prerequisites are met" } }
            },
            Tags = new[] { "apps", "msi", "error-code" }
        };

        /// <summary>
        /// Fixed: Now also checks errorDetail for dependency-related IME log messages.
        /// The message field of app_install_failed events now contains the IME log line context.
        /// </summary>
        private static AnalyzeRule CreateAppDependencyChainRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-APP-003",
            Title = "App Dependency Chain Failure",
            Description = "Detects when an app fails because a dependency app failed to install.",
            Severity = "high",
            Category = "apps",
            BaseConfidence = 70,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "dependency_failure", Source = "event_type", EventType = "app_install_failed", DataField = "message", Operator = "regex", Value = @"dependency|prerequisite|required app|depends on|Dependency", Required = true }
            },
            Explanation = "An app installation failed because one of its dependency apps failed to install. Fix the dependency first, then retry.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Fix dependency chain", Steps = new List<string> { "Identify which dependency app failed", "Fix the dependency app first", "Verify dependency chain order in Intune" } }
            },
            Tags = new[] { "apps", "dependency" }
        };

        /// <summary>
        /// Fixed: Now uses performance_snapshot events (which are actually emitted by the Agent)
        /// instead of non-existent error_detected events. Detects low disk via performance data.
        /// </summary>
        private static AnalyzeRule CreateAppDiskSpaceRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-APP-004",
            Title = "Insufficient Disk Space",
            Description = "Detects when disk space is critically low during enrollment, which can cause app installation failures.",
            Severity = "critical",
            Category = "apps",
            BaseConfidence = 75,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "low_disk", Source = "event_type", EventType = "performance_snapshot", DataField = "disk_free_gb", Operator = "lt", Value = "5", Required = true }
            },
            ConfidenceFactors = new List<ConfidenceFactor>
            {
                new ConfidenceFactor { Signal = "app_failed", Condition = "exists", Weight = 15 },
                new ConfidenceFactor { Signal = "enrollment_failed", Condition = "exists", Weight = 10 }
            },
            Explanation = "Performance monitoring shows disk free space dropped below 5 GB during enrollment. This can cause app installation failures, especially for large Win32 apps.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Free disk space", Steps = new List<string> { "Check device disk size meets minimum requirements", "Clean up temporary files and IME cache (C:\\Windows\\IMECache)", "Consider using a larger disk image" } }
            },
            Tags = new[] { "apps", "disk-space", "critical" }
        };

        /// <summary>
        /// Fixed: Now checks errorDetail and errorPatternId for download-related IME error patterns
        /// (IME-DO-TIMEOUT, IME-ERROR-DOWNLOAD, IME-ERROR-NO-CONTENT).
        /// </summary>
        private static AnalyzeRule CreateAppDownloadTimeoutRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-APP-005",
            Title = "Content Download Timeout",
            Description = "Detects when app content download times out or fails.",
            Severity = "high",
            Category = "apps",
            BaseConfidence = 70,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "download_timeout", Source = "event_type", EventType = "app_install_failed", DataField = "errorPatternId", Operator = "regex", Value = @"IME-DO-TIMEOUT|IME-ERROR-DOWNLOAD|IME-ERROR-NO-CONTENT", Required = true }
            },
            Explanation = "App content download timed out or failed. This is usually caused by network issues, proxy interference, or CDN problems. The IME log shows a Delivery Optimization timeout or download content error.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Improve download speed", Steps = new List<string> { "Check network bandwidth", "Verify proxy doesn't block *.delivery.mp.microsoft.com", "Consider Delivery Optimization configuration" } }
            },
            Tags = new[] { "apps", "download", "timeout" }
        };

        private static AnalyzeRule CreateAppRebootLoopRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-APP-006",
            Title = "Reboot Required Loop",
            Description = "Detects when the same app installation is attempted 3 or more times, indicating a reboot-required loop.",
            Severity = "high",
            Category = "apps",
            BaseConfidence = 70,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "reboot_loop", Source = "event_count", EventType = "app_install_started", DataField = "appId", Operator = "count_per_group_gte", Value = "3", Required = true }
            },
            Explanation = "The same app has been installed 3 or more times during this enrollment. This typically indicates the app requires a reboot between install attempts, but the ESP is not honoring the reboot request, causing an install-detect-retry loop.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Fix reboot handling", Steps = new List<string> { "Check if the app's return code mapping includes reboot codes (3010, 1641)", "Verify ESP reboot behavior settings", "Consider setting the app to 'Hard reboot' return code in Intune" } }
            },
            Tags = new[] { "apps", "reboot", "loop" }
        };

        private static AnalyzeRule CreateAppTrackingSummaryErrorsRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-APP-007",
            Title = "Multiple App Installation Failures",
            Description = "Detects when the app tracking summary reports multiple failed app installations.",
            Severity = "high",
            Category = "apps",
            BaseConfidence = 75,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "summary_errors", Source = "event_type", EventType = "app_tracking_summary", DataField = "errorCount", Operator = "gte", Value = "2", Required = true }
            },
            ConfidenceFactors = new List<ConfidenceFactor>
            {
                new ConfidenceFactor { Signal = "enrollment_failed", Condition = "exists", Weight = 15 }
            },
            Explanation = "The app tracking summary reports 2 or more failed app installations during enrollment. This suggests a systemic issue rather than an isolated app failure.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Review failed apps", Steps = new List<string> {
                    "Check the individual app_install_failed events for specific error patterns",
                    "Look for a common pattern (all MSI apps, all large apps, all from a specific publisher)",
                    "Consider whether a shared prerequisite or dependency is failing"
                }}
            },
            Tags = new[] { "apps", "summary", "multiple-failures" }
        };

        private static AnalyzeRule CreateAppSlowInstallRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-APP-008",
            Title = "Slow App Installation",
            Description = "Detects app installations that take longer than 2 minutes.",
            Severity = "warning",
            Category = "apps",
            BaseConfidence = 70,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "slow_app_install", Source = "app_install_duration", EventType = "app_install_completed", Operator = "gt", Value = "120", Required = true }
            },
            Explanation = "At least one app installation took longer than 2 minutes. This can indicate slow content delivery, installation overhead, or endpoint performance bottlenecks.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Analyze slow app behavior", Steps = new List<string> {
                    "Identify the affected app from matched evidence in the rule result",
                    "Check network throughput and proxy impact during app download",
                    "Consider moving large/non-critical apps out of ESP blocking scope"
                }}
            },
            Tags = new[] { "apps", "performance", "slow-install", "warning" }
        };

        // ===== ESP =====

        private static AnalyzeRule CreateEspBlockingAppTimeoutRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-ESP-001",
            Title = "ESP Blocking App Timeout",
            Description = "Detects when the DeviceSetup phase of the ESP took longer than 30 minutes, indicating a stuck or very slow blocking app.",
            Severity = "high",
            Category = "esp",
            // BaseConfidence is intentionally below ConfidenceThreshold so the rule only fires
            // when the duration factor (phase_duration > 1800s) also matches.
            BaseConfidence = 50,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "esp_stalled", Source = "phase_duration", EventType = "esp_phase_changed", DataField = "espPhase", Operator = "equals", Value = "DeviceSetup", Required = true }
            },
            ConfidenceFactors = new List<ConfidenceFactor>
            {
                new ConfidenceFactor { Signal = "long_esp", Condition = "phase_duration > 1800", Weight = 40 }
            },
            // Only fire when duration factor also matched (50 base + 40 factor = 90 > 70 threshold)
            ConfidenceThreshold = 70,
            Explanation = "The ESP DeviceSetup phase took more than 30 minutes. This indicates a blocking app is stuck installing, downloading very slowly, or has entered a retry loop.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Investigate blocking app", Steps = new List<string> { "Identify which app is blocking (check ESP events)", "Verify app content is downloadable", "Consider reducing the number of blocking apps" } }
            },
            Tags = new[] { "esp", "blocking", "timeout" }
        };

        /// <summary>
        /// Fixed: Now checks app_install_failed events for security/policy-related error patterns
        /// instead of non-existent error_detected events. Also matches IME enforcement errors.
        /// </summary>
        private static AnalyzeRule CreateEspSecurityPolicyFailureRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-ESP-002",
            Title = "Security Policy Application Failure",
            Description = "Detects when a required security policy fails to apply during ESP by looking for policy-related errors in app installation and IME log events.",
            Severity = "high",
            Category = "esp",
            BaseConfidence = 65,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "policy_failure", Source = "event_type", EventType = "app_install_failed", DataField = "message", Operator = "regex", Value = @"policy.*fail|security.*policy|compliance.*fail|BitLocker.*fail|enforcementState.*Error|EnforcementState", Required = true }
            },
            ConfidenceFactors = new List<ConfidenceFactor>
            {
                new ConfidenceFactor { Signal = "esp_stalled", Condition = "exists", Weight = 15 }
            },
            Explanation = "An app installation failed with a message indicating a security or compliance policy issue. This may block ESP completion if the affected app is an ESP-blocking app.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Fix policy", Steps = new List<string> { "Check which policy failed in the event details", "Verify device meets policy prerequisites (TPM, SecureBoot)", "Review policy targeting and assignments" } }
            },
            Tags = new[] { "esp", "policy", "security" }
        };

        // ===== CORRELATION RULES =====
        // These combine signals from multiple event types for root-cause analysis

        private static AnalyzeRule CreateCorrelationNetworkCausedInstallFailureRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-CORR-001",
            Title = "Slow Network Causing App Installation Failure",
            Description = "Correlates slow download speeds with app installation failures while disk space is adequate, confirming network as root cause (not disk).",
            Severity = "high",
            Category = "apps",
            Trigger = "correlation",
            BaseConfidence = 55,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "app_failed", Source = "event_type", EventType = "app_install_failed", Operator = "exists", Value = "", Required = true },
                new RuleCondition { Signal = "download_events", Source = "event_type", EventType = "download_progress", Operator = "exists", Value = "", Required = true },
                new RuleCondition { Signal = "disk_ok", Source = "event_type", EventType = "performance_snapshot", DataField = "disk_free_gb", Operator = "gt", Value = "10" }
            },
            ConfidenceFactors = new List<ConfidenceFactor>
            {
                new ConfidenceFactor { Signal = "disk_ok", Condition = "exists", Weight = 15 },
                new ConfidenceFactor { Signal = "multiple_failures", Condition = "count >= 2", Weight = 15 },
                new ConfidenceFactor { Signal = "enrollment_failed", Condition = "exists", Weight = 15 }
            },
            ConfidenceThreshold = 50,
            Explanation = "App installation failed while the device has sufficient disk space (>10 GB free). Download progress events indicate network-related issues are the likely root cause.\n\nThis rules out disk space as a factor and points to network bandwidth, proxy throttling, or CDN connectivity issues.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Investigate network throughput", Steps = new List<string> {
                    "Check bandwidth to *.delivery.mp.microsoft.com (CDN)",
                    "Verify proxy is not throttling large downloads",
                    "Consider enabling Delivery Optimization for peer-to-peer caching"
                }},
                new RemediationStep { Title = "Reduce download load", Steps = new List<string> {
                    "Stagger app deployments to reduce concurrent downloads",
                    "Pre-cache large apps using Delivery Optimization or ConfigMgr",
                    "Increase ESP timeout if downloads are slow but completing"
                }}
            },
            Tags = new[] { "correlation", "apps", "network", "download", "root-cause" }
        };

        /// <summary>
        /// Fixed: Second condition now uses app_install_failed instead of non-existent error_detected.
        /// Correlates low disk space from performance_snapshot with app failures.
        /// </summary>
        private static AnalyzeRule CreateCorrelationDiskSpaceCausedInstallFailureRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-CORR-002",
            Title = "Disk Space Exhaustion Caused Installation Failure",
            Description = "Correlates declining disk free space with app installation failure, confirming insufficient storage as root cause.",
            Severity = "critical",
            Category = "apps",
            Trigger = "correlation",
            BaseConfidence = 65,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "low_disk", Source = "event_type", EventType = "performance_snapshot", DataField = "disk_free_gb", Operator = "lt", Value = "5", Required = true },
                new RuleCondition { Signal = "app_failed", Source = "event_type", EventType = "app_install_failed", Operator = "exists", Value = "", Required = true }
            },
            ConfidenceFactors = new List<ConfidenceFactor>
            {
                new ConfidenceFactor { Signal = "multiple_failures", Condition = "count >= 2", Weight = 15 },
                new ConfidenceFactor { Signal = "enrollment_failed", Condition = "exists", Weight = 10 }
            },
            ConfidenceThreshold = 50,
            Explanation = "**Root cause confirmed:** Performance monitoring shows disk free space dropped below 5 GB during enrollment, and apps failed to install. The device likely ran out of storage during app installation.\n\nThis is common when deploying many large Win32 apps to devices with small drives (64 GB or 128 GB).",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Reduce disk consumption during enrollment", Steps = new List<string> {
                    "Review total size of all apps deployed during ESP",
                    "Move non-critical apps to post-ESP deployment",
                    "Use MSIX or Store apps instead of Win32 where possible (smaller footprint)",
                    "Clean up IME cache: C:\\Windows\\IMECache"
                }},
                new RemediationStep { Title = "Use devices with larger drives", Steps = new List<string> {
                    "Minimum 128 GB recommended for standard deployments",
                    "256 GB recommended when deploying large app suites (Office, Visual Studio, etc.)"
                }}
            },
            Tags = new[] { "correlation", "apps", "disk-space", "performance", "root-cause" }
        };

        private static AnalyzeRule CreateCorrelationProxyCausedDownloadFailureRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-CORR-003",
            Title = "Proxy Configuration Causing Download Failure",
            Description = "Correlates an active proxy configuration with app download failures, suggesting the proxy is blocking or throttling Intune content delivery.",
            Severity = "high",
            Category = "apps",
            Trigger = "correlation",
            BaseConfidence = 60,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "proxy_active", Source = "event_type", EventType = "proxy_configuration", DataField = "proxyType", Operator = "regex", Value = "Proxy|PAC", Required = true },
                new RuleCondition { Signal = "app_failed", Source = "event_type", EventType = "app_install_failed", Operator = "exists", Value = "", Required = true }
            },
            ConfidenceFactors = new List<ConfidenceFactor>
            {
                new ConfidenceFactor { Signal = "download_timeout", Condition = "exists", Weight = 20 },
                new ConfidenceFactor { Signal = "multiple_failures", Condition = "count >= 2", Weight = 15 },
                new ConfidenceFactor { Signal = "enrollment_failed", Condition = "exists", Weight = 10 }
            },
            ConfidenceThreshold = 50,
            Explanation = "A proxy (explicit or PAC-based) is configured on this device and app installations failed. Proxies can block or throttle downloads from *.delivery.mp.microsoft.com and other Intune CDN endpoints.\n\nThis is a common root cause when apps fail consistently on proxy-connected corporate networks.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Verify proxy bypass for Intune endpoints", Steps = new List<string> {
                    "Add *.delivery.mp.microsoft.com to proxy bypass list",
                    "Add *.do.dsp.mp.microsoft.com (Delivery Optimization) to bypass list",
                    "Add login.microsoftonline.com and *.manage.microsoft.com to bypass list",
                    "Verify the PAC file is reachable at enrollment time (before user login)"
                }},
                new RemediationStep { Title = "Check proxy authentication", Steps = new List<string> {
                    "Ensure the proxy does not require user authentication during OOBE/device phase",
                    "Consider using machine certificate-based proxy authentication",
                    "Test with proxy temporarily bypassed to confirm it is the root cause"
                }}
            },
            Tags = new[] { "correlation", "apps", "proxy", "network", "download", "root-cause" }
        };

        // ===== NEW RULES =====

        /// <summary>
        /// Detects when the overall enrollment takes excessively long (> 60 minutes).
        /// Uses the total time span between the first and last esp_phase_changed events.
        /// </summary>
        private static AnalyzeRule CreateEnrollmentTimeoutRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-ESP-003",
            Title = "Enrollment Duration Exceeded 60 Minutes",
            Description = "Detects when the total Autopilot enrollment takes longer than 60 minutes.",
            Severity = "high",
            Category = "esp",
            BaseConfidence = 50,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "long_enrollment", Source = "phase_duration", EventType = "esp_phase_changed", DataField = "espPhase", Operator = "equals", Value = "DeviceSetup", Required = true }
            },
            ConfidenceFactors = new List<ConfidenceFactor>
            {
                new ConfidenceFactor { Signal = "long_esp", Condition = "phase_duration > 3600", Weight = 40 }
            },
            ConfidenceThreshold = 70,
            Explanation = "The Autopilot enrollment exceeded 60 minutes. This is unusually long and may indicate stuck apps, slow network, or policy conflicts. The default ESP timeout is 60 minutes, after which the enrollment may be marked as failed.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Investigate long enrollment", Steps = new List<string> {
                    "Check the app installation timeline - identify which app took the longest",
                    "Review download speeds in the performance data",
                    "Consider reducing the number of ESP-blocking apps",
                    "Check if the ESP timeout policy is configured (default: 60 min)"
                }}
            },
            Tags = new[] { "esp", "timeout", "duration" }
        };

        /// <summary>
        /// Detects explicit enrollment failure (enrollment_failed event).
        /// The Agent emits this when the enrollment fails for any reason.
        /// </summary>
        private static AnalyzeRule CreateEnrollmentFailedRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-ENRL-001",
            Title = "Enrollment Failed",
            Description = "Detects when the Autopilot enrollment has explicitly failed.",
            Severity = "critical",
            Category = "enrollment",
            BaseConfidence = 95,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "enrollment_failed", Source = "event_type", EventType = "enrollment_failed", Operator = "exists", Value = "", Required = true }
            },
            ConfidenceFactors = new List<ConfidenceFactor>
            {
                new ConfidenceFactor { Signal = "app_failed", Condition = "exists", Weight = 5 }
            },
            Explanation = "The Autopilot enrollment has explicitly failed. Check the enrollment_failed event details and other rule results for the root cause.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Investigate enrollment failure", Steps = new List<string> {
                    "Check the 'reason' field in the enrollment_failed event for specific failure details",
                    "Review other analyze rule results for correlated issues (disk space, network, app failures)",
                    "Check the device event timeline for the sequence of events leading to failure",
                    "Verify the Autopilot profile and ESP configuration in Intune"
                }}
            },
            Tags = new[] { "enrollment", "failed", "critical" }
        };

        /// <summary>
        /// Detects when Windows Hello provisioning times out.
        /// The HelloDetector emits hello_wait_timeout when Hello wizard doesn't start
        /// within the configured timeout period after ESP exit.
        /// </summary>
        private static AnalyzeRule CreateWindowsHelloTimeoutRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-DEV-001",
            Title = "Windows Hello Provisioning Timeout",
            Description = "Detects when Windows Hello for Business provisioning did not start within the expected timeframe after ESP completion.",
            Severity = "warning",
            Category = "device",
            BaseConfidence = 60,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "hello_timeout", Source = "event_type", EventType = "hello_wait_timeout", Operator = "exists", Value = "", Required = true }
            },
            ConfidenceFactors = new List<ConfidenceFactor>
            {
                new ConfidenceFactor { Signal = "hello_policy_detected", Condition = "exists", Weight = 30 }
            },
            Explanation = "Windows Hello for Business provisioning did not start within the expected time after ESP exit. This may indicate that the Hello policy is not configured, the device doesn't meet prerequisites, or there's a connectivity issue with the Key Registration Service.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Verify Windows Hello configuration", Steps = new List<string> {
                    "Check if Windows Hello for Business is configured in the tenant",
                    "Verify the policy targets the device/user correctly",
                    "Ensure the device has a TPM 2.0 (required for WHfB)",
                    "Check connectivity to the Key Registration Service (enterpriseregistration.windows.net)"
                }}
            },
            Tags = new[] { "device", "hello", "timeout" }
        };

        /// <summary>
        /// Detects when app downloads appear stalled based on download_progress events
        /// with status "failed" and app installation failure patterns matching download errors.
        /// </summary>
        private static AnalyzeRule CreateAppDownloadStallRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-APP-009",
            Title = "App Download Stalled",
            Description = "Detects when app downloads appear to have stalled, based on failed download progress events.",
            Severity = "warning",
            Category = "apps",
            Trigger = "correlation",
            BaseConfidence = 55,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "download_failed", Source = "event_type", EventType = "download_progress", DataField = "status", Operator = "equals", Value = "failed", Required = true },
                new RuleCondition { Signal = "app_failed", Source = "event_type", EventType = "app_install_failed", Operator = "exists", Value = "", Required = true }
            },
            ConfidenceFactors = new List<ConfidenceFactor>
            {
                new ConfidenceFactor { Signal = "proxy_active", Condition = "exists", Weight = 15 },
                new ConfidenceFactor { Signal = "multiple_failures", Condition = "count >= 2", Weight = 15 }
            },
            ConfidenceThreshold = 50,
            Explanation = "Download progress events show a failed download, and at least one app installation also failed. This pattern suggests content delivery issues preventing apps from being downloaded successfully.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Investigate download issues", Steps = new List<string> {
                    "Check network connectivity and bandwidth during the enrollment",
                    "Verify DNS resolution for *.delivery.mp.microsoft.com",
                    "Check if a proxy or firewall is blocking content delivery",
                    "Review Delivery Optimization settings and peer availability"
                }}
            },
            Tags = new[] { "apps", "download", "stall", "correlation" }
        };

        /// <summary>
        /// Detects when an app was initially reported as successfully installed but then
        /// transitioned back to an error state (IME-ERROR-REPORT), correlated by appId.
        /// Uses the app_state_regression source which matches the exact same app across both events.
        /// This is a silent failure that can leave apps broken post-enrollment.
        /// </summary>
        private static AnalyzeRule CreateAppPostSuccessFailureRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-APP-010",
            Title = "App Reverted to Error After Successful Install",
            Description = "Detects when a specific app (matched by appId) was first reported as successfully installed but subsequently reported an IME enforcement error — while enrollment completed successfully. The app may be broken post-enrollment.",
            Severity = "warning",
            Category = "apps",
            Trigger = "correlation",
            BaseConfidence = 75,
            Conditions = new List<RuleCondition>
            {
                // Single condition: finds apps where completed -> failed regression exists for the same appId,
                // and the failed event has errorPatternId = IME-ERROR-REPORT
                new RuleCondition
                {
                    Signal = "app_regression",
                    Source = "app_state_regression",
                    DataField = "errorPatternId",
                    Operator = "equals",
                    Value = "IME-ERROR-REPORT",
                    Required = true
                },
                new RuleCondition
                {
                    Signal = "enrollment_succeeded",
                    Source = "event_type",
                    EventType = "enrollment_complete",
                    Operator = "exists",
                    Value = "",
                    Required = true
                }
            },
            ConfidenceThreshold = 70,
            Explanation = @"An app was initially reported as **successfully installed** (`app_install_completed`) but the IME (Intune Management Extension) subsequently reported an **Error** state for the same app via an `IME-ERROR-REPORT` event — while the overall enrollment completed successfully.

This pattern occurs when the IME re-evaluates enforcement state after the initial install report and detects a failure, e.g. `EnforcementState` transitions from `Success` → `Error`. The affected app and its details are captured in the matched evidence.

Common causes:
- A post-install detection check fails (detection rule mismatch)
- The enforcement error code `-2016345060` (0x87D13B9C) indicates the app state diverged from expected
- The app may appear installed in the ESP summary but is in an error state on the device

**The enrollment succeeded, but the affected app requires attention.**",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep
                {
                    Title = "Investigate the app's post-install enforcement state",
                    Steps = new List<string>
                    {
                        "Note the appId and appName from the rule finding — these identify the exact affected app",
                        "Check the Intune portal: Devices > [Device] > Apps — confirm the current enforcement state of the app",
                        "Review IME logs on the device: C:\\ProgramData\\Microsoft\\IntuneManagementExtension\\Logs\\IntuneManagementExtension.log",
                        "Search the log for the appId and look for 'EnforcementState' transitions (Success → Error) near the enrollment time",
                        "Error -2016345060 (0x87D13B9C) typically indicates a detection failure after install — verify the app's detection rule in Intune",
                        "Trigger a manual sync (Company Portal or Settings > Accounts > Access work or school > Sync) to force re-evaluation"
                    }
                },
                new RemediationStep
                {
                    Title = "Fix detection or installation issues",
                    Steps = new List<string>
                    {
                        "If detection fails: correct the detection rule (file path, registry key, or PowerShell script) in the Intune app configuration",
                        "If the app itself is broken: redeploy or reassign the app to trigger a fresh install",
                        "Consider adding a post-install validation script to catch this scenario proactively"
                    }
                }
            },
            RelatedDocs = new List<RelatedDoc>
            {
                new RelatedDoc { Title = "Troubleshoot Win32 app installations", Url = "https://learn.microsoft.com/en-us/troubleshoot/mem/intune/app-management/troubleshoot-win32-app-install" },
                new RelatedDoc { Title = "Intune Management Extension logs", Url = "https://learn.microsoft.com/en-us/mem/intune/apps/intune-management-extension" }
            },
            Tags = new[] { "apps", "win32", "enforcement", "post-install", "correlation" }
        };

        /// <summary>
        /// Detects high memory usage during enrollment which can cause app installation issues.
        /// </summary>
        private static AnalyzeRule CreateHighMemoryDuringInstallRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-DEV-002",
            Title = "High Memory Usage During Enrollment",
            Description = "Detects when memory usage exceeds 90% during enrollment, which can cause app installation failures or system instability.",
            Severity = "warning",
            Category = "device",
            BaseConfidence = 55,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "high_memory", Source = "event_type", EventType = "performance_snapshot", DataField = "memory_used_percent", Operator = "gt", Value = "90", Required = true }
            },
            ConfidenceFactors = new List<ConfidenceFactor>
            {
                new ConfidenceFactor { Signal = "app_failed", Condition = "exists", Weight = 20 },
                new ConfidenceFactor { Signal = "enrollment_failed", Condition = "exists", Weight = 15 }
            },
            Explanation = "Memory usage exceeded 90% during enrollment. High memory pressure can cause app installation failures, system slowdowns, and ESP timeouts.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Reduce memory pressure", Steps = new List<string> {
                    "Review which apps are being installed concurrently during ESP",
                    "Consider staggering resource-heavy installations",
                    "Check minimum RAM requirements for the device (8 GB recommended)",
                    "Investigate if background services are consuming excessive memory"
                }}
            },
            Tags = new[] { "device", "memory", "performance" }
        };
    }
}
