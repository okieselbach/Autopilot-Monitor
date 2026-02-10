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
                CreateAppRebootLoopRule(),

                // ===== ESP RULES =====
                CreateEspBlockingAppTimeoutRule(),
                CreateEspSecurityPolicyFailureRule(),

                // ===== DEVICE RULES =====
                CreateDeviceTpmNotReadyRule(),
                CreateDeviceBitlockerEscrowRule(),
                CreateDeviceSecureBootRule(),

                // ===== DEVICE RULES (continued) =====
                CreateDeviceAadJoinNotDetectedRule(),

                // ===== APP RULES (continued) =====
                CreateAppTrackingSummaryErrorsRule(),

                // ===== CORRELATION RULES (cross-event analysis) =====
                CreateCorrelationNetworkCausedInstallFailureRule(),
                CreateCorrelationDiskSpaceCausedInstallFailureRule(),
                CreateCorrelationProxyCausedDownloadFailureRule(),
            };
        }

        // ===== APPS =====

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
                new RuleCondition { Signal = "detection_failure", Source = "event_type", EventType = "app_install_failed", DataField = "message", Operator = "regex", Value = "detection|not detected|script.*fail|detection.*error", Required = true }
            },
            Explanation = "A Win32 app detection script failed or did not detect the app after installation. This causes the app to appear as 'not installed' despite successful installation.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Fix detection rules", Steps = new List<string> { "Review the app's detection rules in Intune", "Verify file/registry paths are correct", "Test detection script manually on a device" } }
            },
            Tags = new[] { "apps", "detection", "win32" }
        };

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
                new RuleCondition { Signal = "msi_error", Source = "event_type", EventType = "app_install_failed", DataField = "errorCode", Operator = "regex", Value = "^16[0-9]{2}$|^0x80070[0-9a-f]+$", Required = true }
            },
            Explanation = "An MSI installation failed with a specific error code. Common codes: 1603 (Fatal error), 1618 (Another install in progress), 1619 (Package could not be opened), 1625 (Installation prohibited by policy).",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Investigate MSI error", Steps = new List<string> { "Check the MSI log in C:\\Windows\\CCM\\Logs", "Look up the specific error code", "Verify prerequisites are met" } }
            },
            Tags = new[] { "apps", "msi", "error-code" }
        };

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
                new RuleCondition { Signal = "dependency_failure", Source = "event_type", EventType = "app_install_failed", DataField = "message", Operator = "regex", Value = "dependency|prerequisite|required app|depends on", Required = true }
            },
            Explanation = "An app installation failed because one of its dependency apps failed to install. Fix the dependency first, then retry.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Fix dependency chain", Steps = new List<string> { "Identify which dependency app failed", "Fix the dependency app first", "Verify dependency chain order in Intune" } }
            },
            Tags = new[] { "apps", "dependency" }
        };

        private static AnalyzeRule CreateAppDiskSpaceRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-APP-004",
            Title = "Insufficient Disk Space",
            Description = "Detects when app installation fails due to insufficient disk space.",
            Severity = "critical",
            Category = "apps",
            BaseConfidence = 90,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "disk_space", Source = "event_type", EventType = "error_detected", DataField = "message", Operator = "regex", Value = "disk space|storage.*full|not enough space|0x80070070", Required = true }
            },
            Explanation = "The device does not have enough disk space to install the required applications.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Free disk space", Steps = new List<string> { "Check device disk size meets minimum requirements", "Clean up temporary files", "Consider using a larger disk image" } }
            },
            Tags = new[] { "apps", "disk-space", "critical" }
        };

        private static AnalyzeRule CreateAppDownloadTimeoutRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-APP-005",
            Title = "Content Download Timeout",
            Description = "Detects when app content download times out or is very slow.",
            Severity = "high",
            Category = "apps",
            BaseConfidence = 60,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "download_timeout", Source = "event_type", EventType = "app_install_failed", DataField = "message", Operator = "regex", Value = "download.*timeout|content.*download.*fail|CDN.*error|0x80072ee7", Required = true }
            },
            ConfidenceFactors = new List<ConfidenceFactor>
            {
                new ConfidenceFactor { Signal = "slow_download", Condition = "download_rate < 1000000", Weight = 20 }
            },
            Explanation = "App content download timed out or failed. This is usually caused by network issues, proxy interference, or CDN problems.",
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
            Description = "Detects when an app installation enters a reboot-required loop.",
            Severity = "high",
            Category = "apps",
            BaseConfidence = 70,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "reboot_loop", Source = "event_count", EventType = "app_install_started", Operator = "count_gte", Value = "3", Required = true }
            },
            Explanation = "An app appears to be in a reboot loop - it has been attempted multiple times. This may indicate that the app requires a reboot that is not being honored by the ESP.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Fix reboot handling", Steps = new List<string> { "Check if the app's return code mapping includes reboot codes", "Verify ESP reboot behavior settings", "Consider setting the app to 'Hard reboot' return code" } }
            },
            Tags = new[] { "apps", "reboot", "loop" }
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

        private static AnalyzeRule CreateEspSecurityPolicyFailureRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-ESP-002",
            Title = "Security Policy Application Failure",
            Description = "Detects when a required security policy fails to apply during ESP.",
            Severity = "high",
            Category = "esp",
            BaseConfidence = 70,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "policy_failure", Source = "event_type", EventType = "error_detected", DataField = "message", Operator = "regex", Value = "policy.*fail|security.*policy|compliance.*fail|BitLocker.*fail", Required = true }
            },
            Explanation = "A required security policy failed to apply during the ESP device setup phase. This blocks ESP completion.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Fix policy", Steps = new List<string> { "Check which policy failed in the event details", "Verify device meets policy prerequisites (TPM, SecureBoot)", "Review policy targeting and assignments" } }
            },
            Tags = new[] { "esp", "policy", "security" }
        };

        // ===== DEVICE =====

        private static AnalyzeRule CreateDeviceTpmNotReadyRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-DEV-001",
            Title = "TPM Not Ready",
            Description = "Detects when the TPM is not ready or not functioning properly.",
            Severity = "critical",
            Category = "device",
            BaseConfidence = 80,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "tpm_error", Source = "event_type", EventType = "error_detected", DataField = "message", Operator = "regex", Value = "TPM|Trusted Platform Module|0x80284001|0x80290300", Required = true }
            },
            Explanation = "The TPM (Trusted Platform Module) is not ready or functioning. TPM is required for Autopilot, Windows Hello, and BitLocker.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Fix TPM", Steps = new List<string> { "Check BIOS/UEFI settings for TPM enablement", "Update TPM firmware if available", "Clear TPM and re-provision (WARNING: data loss)" } }
            },
            Tags = new[] { "device", "tpm", "critical" }
        };

        private static AnalyzeRule CreateDeviceBitlockerEscrowRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-DEV-002",
            Title = "BitLocker Key Escrow Failure",
            Description = "Detects when BitLocker recovery key fails to escrow to Azure AD.",
            Severity = "high",
            Category = "device",
            BaseConfidence = 70,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "bitlocker_escrow_failure", Source = "event_type", EventType = "error_detected", DataField = "message", Operator = "regex", Value = "BitLocker.*escrow|recovery key.*backup|key.*protector.*fail", Required = true }
            },
            Explanation = "BitLocker recovery key escrow to Azure AD failed. This may block ESP completion if 'Require BitLocker key escrow' is configured.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Fix BitLocker escrow", Steps = new List<string> { "Verify Azure AD connectivity", "Check that the device is properly Azure AD joined", "Manually trigger BitLocker key backup: manage-bde -protectors -adbackup C: -id {ID}" } }
            },
            Tags = new[] { "device", "bitlocker", "escrow" }
        };

        private static AnalyzeRule CreateDeviceSecureBootRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-DEV-003",
            Title = "Secure Boot Disabled",
            Description = "Detects when Secure Boot is disabled, which may be required by compliance policies.",
            Severity = "warning",
            Category = "device",
            BaseConfidence = 60,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "secureboot_disabled", Source = "event_type", EventType = "secureboot_status", DataField = "uefiSecureBootEnabled", Operator = "equals", Value = "False", Required = true }
            },
            Explanation = "Secure Boot is disabled on this device. Some compliance policies require Secure Boot to be enabled.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Enable Secure Boot", Steps = new List<string> { "Enter BIOS/UEFI settings", "Enable Secure Boot", "Ensure the device boots in UEFI mode (not Legacy/CSM)" } }
            },
            Tags = new[] { "device", "secureboot" }
        };

        // ===== CORRELATION RULES =====
        // These combine signals from multiple event types for root-cause analysis

        private static AnalyzeRule CreateDeviceAadJoinNotDetectedRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-DEV-004",
            Title = "Azure AD Join Not Detected",
            Description = "Detects when the device is not Azure AD joined at the time of enrollment.",
            Severity = "critical",
            Category = "device",
            BaseConfidence = 85,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "aad_not_joined", Source = "event_type", EventType = "aad_join_status", DataField = "joinType", Operator = "equals", Value = "Not Joined", Required = true }
            },
            Explanation = "The device was not Azure AD joined at the start of enrollment. Autopilot requires the device to join Azure AD. This will prevent Intune enrollment and policy delivery from succeeding.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Verify Azure AD connectivity", Steps = new List<string> {
                    "Ensure the device can reach login.microsoftonline.com",
                    "Check that the Autopilot profile's tenant matches the signing-in user's tenant",
                    "Verify the device is not already joined to an on-premises domain (Hybrid Join may be required)"
                }}
            },
            Tags = new[] { "device", "aad", "join", "critical" }
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
                    "Check the individual app_install_failed events for specific error codes",
                    "Look for a common pattern (all MSI apps, all large apps, all from a specific publisher)",
                    "Consider whether a shared prerequisite or dependency is failing"
                }}
            },
            Tags = new[] { "apps", "summary", "multiple-failures" }
        };

        // ===== CORRELATION RULES =====
        // These combine signals from multiple event types for root-cause analysis

        private static AnalyzeRule CreateCorrelationNetworkCausedInstallFailureRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-CORR-003",
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

        private static AnalyzeRule CreateCorrelationDiskSpaceCausedInstallFailureRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-CORR-004",
            Title = "Disk Space Exhaustion Caused Installation Failure",
            Description = "Correlates declining disk free space with app installation failure, confirming insufficient storage as root cause.",
            Severity = "critical",
            Category = "apps",
            Trigger = "correlation",
            BaseConfidence = 65,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "low_disk", Source = "event_type", EventType = "performance_snapshot", DataField = "disk_free_gb", Operator = "lt", Value = "5", Required = true },
                new RuleCondition { Signal = "install_error", Source = "event_type", EventType = "error_detected", DataField = "message", Operator = "regex", Value = "disk space|storage|not enough space|0x80070070|0x80070008|ERROR_DISK_FULL", Required = true }
            },
            ConfidenceFactors = new List<ConfidenceFactor>
            {
                new ConfidenceFactor { Signal = "app_failed", Condition = "exists", Weight = 20 },
                new ConfidenceFactor { Signal = "enrollment_failed", Condition = "exists", Weight = 10 }
            },
            ConfidenceThreshold = 50,
            Explanation = "**Root cause confirmed:** Performance monitoring shows disk free space dropped below 5 GB during enrollment, and error events contain disk-space-related error codes. The device ran out of storage during app installation.\n\nThis is common when deploying many large Win32 apps to devices with small drives (64 GB or 128 GB).",
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
            RuleId = "ANALYZE-CORR-005",
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
    }
}
