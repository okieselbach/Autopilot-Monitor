using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.Core.Monitoring.Collectors
{
    /// <summary>
    /// Partial: Polls ESP provisioning category status from registry to detect failures
    /// that Shell-Core event 62407 patterns may miss (e.g. Certificate provisioning failures).
    ///
    /// Registry path: HKLM\SOFTWARE\Microsoft\Provisioning\AutopilotSettings
    /// Values: DevicePreparationCategory.Status, DeviceSetupCategory.Status, AccountSetupCategory.Status
    /// Each value is a JSON string with categorySucceeded (bool) and categoryStatusMessage (string).
    /// </summary>
    public partial class EspAndHelloTracker
    {
        private const string ProvisioningStatusRegistryPath = @"SOFTWARE\Microsoft\Provisioning\AutopilotSettings";
        private const int ProvisioningStatusPollIntervalSeconds = 15;

        private static readonly string[] ProvisioningCategoryNames =
        {
            "DevicePreparationCategory.Status",
            "DeviceSetupCategory.Status",
            "AccountSetupCategory.Status"
        };

        // Last known JSON per category — only emit events on changes
        private Dictionary<string, string> _lastProvisioningStatus;
        // Fire-once guard per category — prevent duplicate EspFailureDetected calls
        private HashSet<string> _provisioningFailureFired;
        // Track which categories have reported a final categorySucceeded value
        private HashSet<string> _provisioningCategoriesResolved;

        private void StartProvisioningStatusPolling()
        {
            _lastProvisioningStatus = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _provisioningFailureFired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _provisioningCategoriesResolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            _provisioningStatusTimer = new System.Threading.Timer(
                CheckProvisioningStatus,
                null,
                TimeSpan.FromSeconds(10),  // Initial delay — give enrollment time to start writing values
                TimeSpan.FromSeconds(ProvisioningStatusPollIntervalSeconds)
            );

            _logger.Info($"Provisioning status polling started (interval: {ProvisioningStatusPollIntervalSeconds}s)");
        }

        private void StopProvisioningStatusPolling(string reason)
        {
            if (_provisioningStatusTimer == null)
                return;

            try
            {
                _provisioningStatusTimer.Dispose();
                _provisioningStatusTimer = null;
                _logger.Info($"Provisioning status polling stopped: {reason}");
            }
            catch (Exception ex)
            {
                _logger.Error("Error stopping provisioning status polling timer", ex);
            }
        }

        private void CheckProvisioningStatus(object state)
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(ProvisioningStatusRegistryPath, writable: false))
                {
                    if (key == null)
                        return; // Key not yet created — enrollment hasn't started writing status

                    bool failureDetected = false;
                    string failureType = null;

                    foreach (var categoryName in ProvisioningCategoryNames)
                    {
                        var jsonValue = key.GetValue(categoryName)?.ToString();
                        if (string.IsNullOrEmpty(jsonValue))
                            continue;

                        // Skip if unchanged since last poll
                        if (_lastProvisioningStatus.TryGetValue(categoryName, out var lastJson) && lastJson == jsonValue)
                            continue;

                        _lastProvisioningStatus[categoryName] = jsonValue;
                        var result = ProcessCategoryStatus(categoryName, jsonValue);

                        if (result.HasValue && !result.Value)
                        {
                            failureDetected = true;
                            failureType = result.FailureType;
                        }
                    }

                    // Self-termination: all observed categories have reported a final status
                    if (_provisioningCategoriesResolved.Count > 0
                        && _lastProvisioningStatus.Count > 0
                        && _provisioningCategoriesResolved.Count >= _lastProvisioningStatus.Count)
                    {
                        StopProvisioningStatusPolling("all_resolved");
                    }

                    // Fire EspFailureDetected AFTER timer stop decision and AFTER event emission
                    // (matches the pattern in ShellCoreTracking.cs — event in spool before agent reacts)
                    if (failureDetected && failureType != null)
                    {
                        StopProvisioningStatusPolling("failure_detected");

                        try
                        {
                            EspFailureDetected?.Invoke(this, failureType);
                        }
                        catch (Exception ex)
                        {
                            _logger.Error($"EspFailureDetected handler failed for '{failureType}'", ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Provisioning status polling failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Processes a single category status JSON value. Emits an info event for timeline visibility.
        /// Returns a result indicating whether the category succeeded, failed, or couldn't be determined.
        /// </summary>
        private CategoryStatusResult ProcessCategoryStatus(string categoryName, string jsonValue)
        {
            var categoryLabel = categoryName.Replace("Category.Status", "");

            try
            {
                using (var doc = JsonDocument.Parse(jsonValue))
                {
                    var root = doc.RootElement;

                    // Extract core fields
                    bool? categorySucceeded = null;
                    string categoryStatusMessage = null;

                    if (root.TryGetProperty("categorySucceeded", out var succeededProp))
                        categorySucceeded = succeededProp.GetBoolean();

                    if (root.TryGetProperty("categoryStatusMessage", out var messageProp))
                        categoryStatusMessage = messageProp.GetString();

                    // Collect subcategory details
                    var subcategories = new Dictionary<string, string>();
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.Name.Contains("Subcategory", StringComparison.OrdinalIgnoreCase))
                            subcategories[prop.Name] = prop.Value.GetString() ?? "";
                    }

                    // Determine severity based on outcome
                    var severity = categorySucceeded == false ? EventSeverity.Warning : EventSeverity.Info;
                    var statusText = categoryStatusMessage ?? (categorySucceeded == true ? "Complete" : categorySucceeded == false ? "Failed" : "Unknown");

                    // Build event data
                    var eventData = new Dictionary<string, object>
                    {
                        { "category", categoryLabel },
                        { "categorySucceeded", categorySucceeded?.ToString() ?? "unknown" },
                        { "categoryStatusMessage", statusText }
                    };

                    // Add subcategory details
                    if (subcategories.Count > 0)
                        eventData["subcategories"] = subcategories;

                    // Emit timeline event for every status change
                    _onEventCollected(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        EventType = Constants.EventTypes.EspProvisioningStatus,
                        Severity = severity,
                        Source = "EspAndHelloTracker",
                        Phase = EnrollmentPhase.Unknown,
                        Message = $"ESP provisioning status: {categoryLabel} — {statusText}",
                        Data = eventData
                    });

                    _logger.Info($"Provisioning status: {categoryLabel} — succeeded={categorySucceeded}, message={statusText}");

                    // Track resolved categories (both success and failure are final)
                    if (categorySucceeded.HasValue)
                        _provisioningCategoriesResolved.Add(categoryName);

                    // Handle failure
                    if (categorySucceeded == false)
                    {
                        if (_provisioningFailureFired.Contains(categoryName))
                            return CategoryStatusResult.AlreadyFired;

                        _provisioningFailureFired.Add(categoryName);

                        var failureType = ExtractProvisioningFailureType(categoryLabel, subcategories);
                        _logger.Warning($"Provisioning failure detected: {failureType}");

                        return CategoryStatusResult.Failed(failureType);
                    }

                    return categorySucceeded == true
                        ? CategoryStatusResult.Succeeded
                        : CategoryStatusResult.Unknown;
                }
            }
            catch (JsonException ex)
            {
                _logger.Warning($"Failed to parse provisioning status JSON for {categoryLabel}: {ex.Message}");
                return CategoryStatusResult.Unknown;
            }
        }

        /// <summary>
        /// Builds a structured failure type string from the category and its subcategories.
        /// E.g. "Provisioning_DeviceSetup_Certificates_Failed" when the Certificates subcategory failed.
        /// </summary>
        private static string ExtractProvisioningFailureType(string categoryLabel, Dictionary<string, string> subcategories)
        {
            // Try to find which subcategory failed by looking for values that don't indicate success
            foreach (var kvp in subcategories)
            {
                var value = kvp.Value;
                // Success indicators in subcategory values
                if (value.Contains("Complete", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("applied", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("installed", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("added", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("No setup needed", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("Succeeded", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("Identified", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // This subcategory did not succeed — extract a clean name
                // E.g. "CertificatesSubcategory" -> "Certificates"
                //      "SecurityPoliciesSubcategory" -> "SecurityPolicies"
                //      "AccountSetup.CertificatesSubcategory" -> "Certificates"
                var subcategoryName = kvp.Key
                    .Replace("Subcategory", "")
                    .Replace("AccountSetup.", "")
                    .Replace("DeviceSetup.", "");

                // Remove any remaining dots for clean naming
                if (subcategoryName.Contains('.'))
                    subcategoryName = subcategoryName.Split('.').Last();

                return $"Provisioning_{categoryLabel}_{subcategoryName}_Failed";
            }

            return $"Provisioning_{categoryLabel}_Failed";
        }

        /// <summary>
        /// Result of processing a single category status value.
        /// </summary>
        private readonly struct CategoryStatusResult
        {
            public readonly bool HasValue;
            public readonly bool Value; // true = succeeded, false = failed
            public readonly string FailureType;

            private CategoryStatusResult(bool hasValue, bool value, string failureType = null)
            {
                HasValue = hasValue;
                Value = value;
                FailureType = failureType;
            }

            public static CategoryStatusResult Succeeded => new CategoryStatusResult(true, true);
            public static CategoryStatusResult AlreadyFired => new CategoryStatusResult(false, false);
            public static CategoryStatusResult Unknown => new CategoryStatusResult(false, false);
            public static CategoryStatusResult Failed(string failureType) => new CategoryStatusResult(true, false, failureType);
        }
    }
}
