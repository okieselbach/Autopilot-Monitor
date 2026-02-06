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
                // ===== NETWORK RULES =====
                CreateNetworkProxyAuthRule(),
                CreateNetworkDnsFailureRule(),
                CreateNetworkSslInspectionRule(),
                CreateNetworkCaptivePortalRule(),
                CreateNetworkMeteredConnectionRule(),

                // ===== IDENTITY RULES =====
                CreateIdentityAadJoinTimeoutRule(),
                CreateIdentityDeviceLimitRule(),
                CreateIdentityTimeSyncRule(),
                CreateIdentityConditionalAccessRule(),

                // ===== ENROLLMENT RULES =====
                CreateEnrollmentRestrictionRule(),
                CreateEnrollmentTermsOfUseRule(),

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

                // ===== CORRELATION RULES (cross-event analysis) =====
                CreateCorrelationProxyAuthRule(),
                CreateCorrelationSslCertFailureRule(),
                CreateCorrelationNetworkCausedInstallFailureRule(),
                CreateCorrelationDiskSpaceCausedInstallFailureRule(),
                CreateCorrelationTpmIdentityFailureRule()
            };
        }

        // ===== NETWORK =====

        private static AnalyzeRule CreateNetworkProxyAuthRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-NET-001",
            Title = "Proxy Authentication Required",
            Description = "Detects when enrollment fails due to proxy requiring authentication that the SYSTEM account cannot provide.",
            Severity = "high",
            Category = "network",
            BaseConfidence = 50,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "http_407_error", Source = "event_type", EventType = "error_detected", DataField = "errorCode", Operator = "contains", Value = "407", Required = true },
                new RuleCondition { Signal = "proxy_configured", Source = "event_type", EventType = "gather_proxy_settings", DataField = "ProxyEnable", Operator = "equals", Value = "1" }
            },
            ConfidenceFactors = new List<ConfidenceFactor>
            {
                new ConfidenceFactor { Signal = "proxy_configured", Condition = "exists", Weight = 20 },
                new ConfidenceFactor { Signal = "enrollment_stalled", Condition = "phase_duration > 300", Weight = 30 }
            },
            Explanation = "The device is behind a proxy that requires authentication. During Autopilot enrollment, operations run under the SYSTEM account, which cannot provide user credentials to authenticate with the proxy.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Configure proxy bypass", Steps = new List<string> { "Add *.manage.microsoft.com to proxy bypass list", "Add *.microsoftonline.com to proxy bypass list", "Add enterpriseregistration.windows.net to bypass list" } },
                new RemediationStep { Title = "Use PAC file", Steps = new List<string> { "Create/update PAC file to bypass proxy for Microsoft endpoints", "Deploy via GPO or Intune" } }
            },
            RelatedDocs = new List<RelatedDoc>
            {
                new RelatedDoc { Title = "Intune network requirements", Url = "https://learn.microsoft.com/en-us/mem/intune/fundamentals/intune-endpoints" },
                new RelatedDoc { Title = "Autopilot networking requirements", Url = "https://learn.microsoft.com/en-us/mem/autopilot/networking-requirements" }
            },
            Tags = new[] { "network", "proxy", "authentication", "common" }
        };

        private static AnalyzeRule CreateNetworkDnsFailureRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-NET-002",
            Title = "DNS Resolution Failure",
            Description = "Detects DNS resolution failures for critical Microsoft enrollment endpoints.",
            Severity = "high",
            Category = "network",
            BaseConfidence = 60,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "dns_failure", Source = "event_type", EventType = "error_detected", DataField = "message", Operator = "regex", Value = "DNS|name resolution|NXDOMAIN|could not resolve", Required = true }
            },
            Explanation = "DNS resolution is failing for critical Microsoft endpoints. This prevents the device from reaching Intune and Azure AD services.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Verify DNS configuration", Steps = new List<string> { "Check DNS server configuration", "Verify DNS resolution for login.microsoftonline.com", "Verify DNS resolution for enterpriseregistration.windows.net" } }
            },
            Tags = new[] { "network", "dns" }
        };

        private static AnalyzeRule CreateNetworkSslInspectionRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-NET-003",
            Title = "SSL Inspection Breaking Enrollment",
            Description = "Detects SSL/TLS inspection that may interfere with certificate-based enrollment communication.",
            Severity = "high",
            Category = "network",
            BaseConfidence = 70,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "ssl_inspection", Source = "event_type", EventType = "cert_validation", DataField = "ssl_inspection_detected", Operator = "equals", Value = "true", Required = true }
            },
            Explanation = "SSL/TLS inspection (man-in-the-middle) is detected on critical enrollment endpoints. This can break certificate pinning and cause enrollment failures.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Bypass SSL inspection", Steps = new List<string> { "Add *.manage.microsoft.com to SSL inspection bypass list", "Add *.microsoftonline.com to bypass list", "Add *.windows.net to bypass list" } }
            },
            Tags = new[] { "network", "ssl", "certificate" }
        };

        private static AnalyzeRule CreateNetworkCaptivePortalRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-NET-004",
            Title = "Captive Portal Detected",
            Description = "Detects when the device is stuck behind a captive portal during enrollment.",
            Severity = "warning",
            Category = "network",
            BaseConfidence = 60,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "captive_portal", Source = "event_type", EventType = "error_detected", DataField = "message", Operator = "regex", Value = "captive|portal|redirect|302.*login", Required = true }
            },
            Explanation = "A captive portal is redirecting network traffic. During OOBE, the device cannot interact with captive portals, preventing enrollment.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Bypass captive portal for enrollment", Steps = new List<string> { "Whitelist device MAC addresses on the network", "Use a pre-authenticated VLAN for Autopilot devices", "Configure the captive portal to bypass Microsoft endpoints" } }
            },
            Tags = new[] { "network", "captive-portal" }
        };

        private static AnalyzeRule CreateNetworkMeteredConnectionRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-NET-005",
            Title = "Metered Connection Blocking Downloads",
            Description = "Detects when a metered connection may be blocking app downloads.",
            Severity = "warning",
            Category = "network",
            BaseConfidence = 40,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "metered_connection", Source = "event_type", EventType = "gather_network_adapters", DataField = "isMetered", Operator = "equals", Value = "true", Required = true }
            },
            Explanation = "The network connection is marked as metered. Windows may restrict downloads over metered connections, slowing or blocking app installations.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Configure non-metered connection", Steps = new List<string> { "Connect to a non-metered network", "Configure the connection as non-metered via GPO" } }
            },
            Tags = new[] { "network", "metered" }
        };

        // ===== IDENTITY =====

        private static AnalyzeRule CreateIdentityAadJoinTimeoutRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-ID-001",
            Title = "Azure AD Join Timeout",
            Description = "Detects when Azure AD join is taking too long or timing out.",
            Severity = "high",
            Category = "identity",
            BaseConfidence = 60,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "identity_phase_stalled", Source = "phase_duration", EventType = "phase_changed", DataField = "currentPhase", Operator = "equals", Value = "Identity", Required = true }
            },
            ConfidenceFactors = new List<ConfidenceFactor>
            {
                new ConfidenceFactor { Signal = "long_duration", Condition = "phase_duration > 180", Weight = 30 }
            },
            Explanation = "The Azure AD / Entra ID join process is taking longer than expected. This may indicate network issues, conditional access policies, or AAD service problems.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Verify connectivity", Steps = new List<string> { "Check connectivity to login.microsoftonline.com", "Check connectivity to enterpriseregistration.windows.net" } },
                new RemediationStep { Title = "Check Conditional Access", Steps = new List<string> { "Review CA policies that may block device enrollment", "Check for MFA requirements during join" } }
            },
            Tags = new[] { "identity", "aad", "timeout" }
        };

        private static AnalyzeRule CreateIdentityDeviceLimitRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-ID-002",
            Title = "Device Limit Reached",
            Description = "Detects when Azure AD device registration limit has been reached.",
            Severity = "high",
            Category = "identity",
            BaseConfidence = 80,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "device_limit", Source = "event_type", EventType = "error_detected", DataField = "message", Operator = "regex", Value = "device limit|maximum.*device|MaximumDevicesPerUser", Required = true }
            },
            Explanation = "The Azure AD device limit for this user has been reached. The user has registered the maximum number of devices allowed by the tenant policy.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Increase device limit", Steps = new List<string> { "Azure Portal > Azure AD > Devices > Device settings", "Increase 'Maximum number of devices per user'" } },
                new RemediationStep { Title = "Remove stale devices", Steps = new List<string> { "Remove old/unused device registrations for the user", "Use Azure AD > Devices to find and delete stale entries" } }
            },
            Tags = new[] { "identity", "device-limit" }
        };

        private static AnalyzeRule CreateIdentityTimeSyncRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-ID-003",
            Title = "Time Synchronization Drift",
            Description = "Detects when device time is significantly out of sync, causing certificate and token validation failures.",
            Severity = "warning",
            Category = "identity",
            BaseConfidence = 70,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "time_sync_error", Source = "event_type", EventType = "error_detected", DataField = "message", Operator = "regex", Value = "time|clock|timestamp.*invalid|certificate.*expired|token.*expired|not yet valid", Required = true }
            },
            Explanation = "The device clock appears to be significantly out of sync. This causes certificate validation failures and token expiration issues.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Sync device time", Steps = new List<string> { "Ensure NTP access (time.windows.com, port 123/UDP)", "Verify BIOS/UEFI clock is correct", "Check for expired CMOS battery" } }
            },
            Tags = new[] { "identity", "time-sync" }
        };

        private static AnalyzeRule CreateIdentityConditionalAccessRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-ID-004",
            Title = "Conditional Access Policy Blocking",
            Description = "Detects when Conditional Access policies are preventing enrollment.",
            Severity = "high",
            Category = "identity",
            BaseConfidence = 70,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "ca_block", Source = "event_type", EventType = "error_detected", DataField = "message", Operator = "regex", Value = "conditional access|AADSTS53003|AADSTS50076|blocked by.*policy|access.*denied.*policy", Required = true }
            },
            Explanation = "A Conditional Access policy is blocking the device or user from completing enrollment. Common causes include MFA requirements, compliant device requirements, or location-based policies.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Review CA policies", Steps = new List<string> { "Check Azure AD > Security > Conditional Access", "Look for policies targeting 'Microsoft Intune Enrollment'", "Ensure Autopilot devices are excluded from blocking policies" } }
            },
            Tags = new[] { "identity", "conditional-access" }
        };

        // ===== ENROLLMENT =====

        private static AnalyzeRule CreateEnrollmentRestrictionRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-ENR-001",
            Title = "Enrollment Restriction Blocking",
            Description = "Detects when Intune enrollment restrictions prevent device enrollment.",
            Severity = "high",
            Category = "enrollment",
            BaseConfidence = 80,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "enrollment_restriction", Source = "event_type", EventType = "error_detected", DataField = "message", Operator = "regex", Value = "enrollment restriction|not allowed.*enroll|platform.*restriction|device type.*not allowed", Required = true }
            },
            Explanation = "An Intune enrollment restriction is preventing this device from enrolling. This could be a platform restriction, device type restriction, or device limit restriction.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Check enrollment restrictions", Steps = new List<string> { "Intune > Devices > Enrollment restrictions", "Verify Windows platform is allowed", "Check device limit restrictions", "Verify the user is in the correct group" } }
            },
            Tags = new[] { "enrollment", "restriction" }
        };

        private static AnalyzeRule CreateEnrollmentTermsOfUseRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-ENR-002",
            Title = "Terms of Use Blocking Enrollment",
            Description = "Detects when Terms of Use acceptance is required but cannot be completed during OOBE.",
            Severity = "warning",
            Category = "enrollment",
            BaseConfidence = 60,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "terms_of_use", Source = "event_type", EventType = "error_detected", DataField = "message", Operator = "regex", Value = "terms of use|ToU|consent required|AADSTS65004", Required = true }
            },
            Explanation = "Terms of Use acceptance is required but the OOBE flow cannot display the ToU page. This blocks enrollment completion.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Exclude from ToU", Steps = new List<string> { "Create a CA policy exclusion for Autopilot devices", "Or accept ToU during pre-provisioning instead" } }
            },
            Tags = new[] { "enrollment", "terms-of-use" }
        };

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
                new RuleCondition { Signal = "reboot_loop", Source = "event_count", EventType = "app_install_start", Operator = "count_gte", Value = "3", Required = true }
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
            Description = "Detects when the ESP is waiting too long for a blocking app to install.",
            Severity = "high",
            Category = "esp",
            BaseConfidence = 60,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "esp_stalled", Source = "phase_duration", EventType = "phase_changed", DataField = "currentPhase", Operator = "equals", Value = "EspDeviceSetup", Required = true }
            },
            ConfidenceFactors = new List<ConfidenceFactor>
            {
                new ConfidenceFactor { Signal = "long_esp", Condition = "phase_duration > 1800", Weight = 30 }
            },
            Explanation = "The Enrollment Status Page has been waiting for a blocking app for an extended period. This often indicates the app is stuck installing or the ESP timeout is about to trigger.",
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
                new RuleCondition { Signal = "secureboot_disabled", Source = "event_type", EventType = "gather_secureboot_status", DataField = "UEFISecureBootEnabled", Operator = "equals", Value = "0", Required = true }
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

        private static AnalyzeRule CreateCorrelationProxyAuthRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-CORR-001",
            Title = "Proxy Authentication Blocking Enrollment",
            Description = "Correlates proxy configuration, HTTP 407 errors, and enrollment stall to confirm proxy authentication is the root cause.",
            Severity = "critical",
            Category = "network",
            Trigger = "correlation",
            BaseConfidence = 60,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "proxy_configured", Source = "event_type", EventType = "gather_proxy_settings", DataField = "ProxyEnable", Operator = "equals", Value = "1", Required = true },
                new RuleCondition { Signal = "auth_error", Source = "event_type", EventType = "error_detected", DataField = "message", Operator = "regex", Value = "407|proxy.*auth|ProxyAuthenticationRequired", Required = true },
                new RuleCondition { Signal = "enrollment_failed", Source = "event_type", EventType = "enrollment_failed", Operator = "exists", Value = "" }
            },
            ConfidenceFactors = new List<ConfidenceFactor>
            {
                new ConfidenceFactor { Signal = "enrollment_failed", Condition = "exists", Weight = 20 },
                new ConfidenceFactor { Signal = "long_network_phase", Condition = "phase_duration > 180", Weight = 15 }
            },
            ConfidenceThreshold = 50,
            Explanation = "**Root cause confirmed:** The device is behind an authenticated proxy (ProxyEnable=1) and received HTTP 407 errors during enrollment. The SYSTEM account cannot authenticate with the proxy, causing enrollment to fail.\n\nThis is a common issue in corporate environments with SSL-inspecting proxies.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Configure proxy bypass for Microsoft endpoints", Steps = new List<string> {
                    "Add *.manage.microsoft.com to proxy bypass list",
                    "Add *.microsoftonline.com to proxy bypass list",
                    "Add enterpriseregistration.windows.net to bypass list",
                    "Add *.delivery.mp.microsoft.com to bypass list (app downloads)"
                }},
                new RemediationStep { Title = "Use PAC file with exceptions", Steps = new List<string> {
                    "Create/update PAC file to bypass proxy for Microsoft endpoints",
                    "Deploy PAC file via GPO or DHCP Option 252",
                    "Ensure PAC file is accessible during OOBE (before user login)"
                }}
            },
            RelatedDocs = new List<RelatedDoc>
            {
                new RelatedDoc { Title = "Autopilot networking requirements", Url = "https://learn.microsoft.com/en-us/mem/autopilot/networking-requirements" },
                new RelatedDoc { Title = "Intune network endpoints", Url = "https://learn.microsoft.com/en-us/mem/intune/fundamentals/intune-endpoints" }
            },
            Tags = new[] { "correlation", "network", "proxy", "authentication", "root-cause" }
        };

        private static AnalyzeRule CreateCorrelationSslCertFailureRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-CORR-002",
            Title = "SSL Inspection Breaking Certificate Enrollment",
            Description = "Correlates SSL inspection detection with certificate validation errors and enrollment failure to confirm SSL inspection as root cause.",
            Severity = "critical",
            Category = "network",
            Trigger = "correlation",
            BaseConfidence = 65,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "ssl_inspection", Source = "event_type", EventType = "cert_validation", DataField = "ssl_inspection_detected", Operator = "equals", Value = "true", Required = true },
                new RuleCondition { Signal = "cert_error", Source = "event_type", EventType = "error_detected", DataField = "message", Operator = "regex", Value = "certificate|SSL|TLS|trust|chain|CERT_E_|0x800b", Required = true },
                new RuleCondition { Signal = "enrollment_failed", Source = "event_type", EventType = "enrollment_failed", Operator = "exists", Value = "" }
            },
            ConfidenceFactors = new List<ConfidenceFactor>
            {
                new ConfidenceFactor { Signal = "enrollment_failed", Condition = "exists", Weight = 20 },
                new ConfidenceFactor { Signal = "missing_mdm_cert", Condition = "exists", Weight = 15 }
            },
            ConfidenceThreshold = 50,
            Explanation = "**Root cause confirmed:** SSL/TLS inspection (MITM proxy) is intercepting connections to Microsoft enrollment endpoints. The intercepted certificates fail validation, breaking the MDM enrollment certificate chain.\n\nThe SSL inspection device replaces Microsoft's certificates with its own, which the enrollment client does not trust.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Bypass SSL inspection for Microsoft endpoints", Steps = new List<string> {
                    "Add *.manage.microsoft.com to SSL inspection bypass list",
                    "Add *.microsoftonline.com to bypass list",
                    "Add *.windows.net to bypass list",
                    "Add *.microsoft.com to bypass list for broader coverage"
                }},
                new RemediationStep { Title = "Deploy inspection CA certificate", Steps = new List<string> {
                    "Export the SSL inspection appliance's root CA certificate",
                    "Deploy it to devices via Autopilot deployment profile or OEM provisioning",
                    "Note: This is a workaround; bypass is the recommended approach"
                }}
            },
            Tags = new[] { "correlation", "network", "ssl", "certificate", "root-cause" }
        };

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

        private static AnalyzeRule CreateCorrelationTpmIdentityFailureRule() => new AnalyzeRule
        {
            RuleId = "ANALYZE-CORR-005",
            Title = "TPM Issue Blocking Identity Enrollment",
            Description = "Correlates TPM not-ready status with identity phase failure and missing device certificates to confirm TPM as root cause.",
            Severity = "critical",
            Category = "device",
            Trigger = "correlation",
            BaseConfidence = 60,
            Conditions = new List<RuleCondition>
            {
                new RuleCondition { Signal = "tpm_issue", Source = "event_type", EventType = "error_detected", DataField = "message", Operator = "regex", Value = "TPM|Trusted Platform|0x80284|0x80290|attestation", Required = true },
                new RuleCondition { Signal = "identity_stalled", Source = "phase_duration", EventType = "phase_changed", DataField = "currentPhase", Operator = "equals", Value = "Identity", Required = true }
            },
            ConfidenceFactors = new List<ConfidenceFactor>
            {
                new ConfidenceFactor { Signal = "long_identity", Condition = "phase_duration > 300", Weight = 20 },
                new ConfidenceFactor { Signal = "enrollment_failed", Condition = "exists", Weight = 15 }
            },
            ConfidenceThreshold = 50,
            Explanation = "**Root cause confirmed:** TPM-related errors occurred during the identity phase, which also took longer than expected. The TPM is either not ready, not provisioned, or malfunctioning, preventing Azure AD join and device attestation.\n\nTPM issues block the entire enrollment chain since device identity cannot be established.",
            Remediation = new List<RemediationStep>
            {
                new RemediationStep { Title = "Fix TPM", Steps = new List<string> {
                    "Enter BIOS/UEFI and verify TPM is enabled",
                    "Update TPM firmware to latest version",
                    "Try clearing TPM: tpm.msc > Clear TPM (WARNING: removes keys)",
                    "Verify TPM version is 2.0 (required for Autopilot)"
                }},
                new RemediationStep { Title = "Pre-provision TPM", Steps = new List<string> {
                    "Run 'Initialize-Tpm' in elevated PowerShell before enrollment",
                    "For new devices, ensure OEM has provisioned TPM during manufacturing",
                    "Check for known TPM firmware issues with your hardware vendor"
                }}
            },
            RelatedDocs = new List<RelatedDoc>
            {
                new RelatedDoc { Title = "Autopilot hardware requirements", Url = "https://learn.microsoft.com/en-us/mem/autopilot/software-requirements" }
            },
            Tags = new[] { "correlation", "device", "tpm", "identity", "root-cause" }
        };
    }
}
