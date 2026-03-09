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
    ///
    /// JSON format varies across Windows versions:
    ///   Flat:   { "CertificatesSubcategory": "Certificates (1 of 1 applied)", "categorySucceeded": true, ... }
    ///   Nested: { "CertificatesSubcategory": { "subcategoryState": "succeeded", "subcategoryStatusText": "..." }, ... }
    ///
    /// categorySucceeded (bool) appears only once the category has finalized.
    /// Until then, subcategory states ("succeeded"/"failed"/...) provide progressive status.
    ///
    /// Event emission strategy:
    ///   - Emit once when a category first appears in the registry (initial snapshot)
    ///   - Emit once when categorySucceeded resolves to true or false (final outcome)
    ///   - Do NOT emit on every intermediate subcategory text change (avoids spam)
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

        // Track the raw JSON per category — used to detect any changes at all
        private Dictionary<string, string> _lastProvisioningJson;
        // Track which categories have been seen (for first-seen event)
        private HashSet<string> _provisioningCategorySeen;
        // Track the last known categorySucceeded per category (null = not yet resolved)
        private Dictionary<string, bool?> _lastCategorySucceeded;
        // Fire-once guard per category — prevent duplicate EspFailureDetected calls
        private HashSet<string> _provisioningFailureFired;
        // Track which categories have reported a final categorySucceeded value
        private HashSet<string> _provisioningCategoriesResolved;

        private void StartProvisioningStatusPolling()
        {
            _lastProvisioningJson = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _provisioningCategorySeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _lastCategorySucceeded = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
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

                        // Skip if JSON is identical to last poll (no change at all)
                        if (_lastProvisioningJson.TryGetValue(categoryName, out var lastJson) && lastJson == jsonValue)
                            continue;

                        _lastProvisioningJson[categoryName] = jsonValue;
                        var result = ProcessCategoryStatus(categoryName, jsonValue);

                        if (result.IsFailed)
                        {
                            failureDetected = true;
                            failureType = result.FailureType;
                        }
                    }

                    // Self-termination: all observed categories have reported a final categorySucceeded
                    if (_provisioningCategoriesResolved.Count > 0
                        && _lastProvisioningJson.Count > 0
                        && _provisioningCategoriesResolved.Count >= _lastProvisioningJson.Count)
                    {
                        StopProvisioningStatusPolling("all_resolved");
                    }

                    // Fire EspFailureDetected AFTER event emission
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
        /// Processes a single category status JSON value.
        /// Emits events only on first-seen and on categorySucceeded resolution (not on every intermediate change).
        /// </summary>
        private ProvisioningResult ProcessCategoryStatus(string categoryName, string jsonValue)
        {
            var categoryLabel = categoryName.Replace("Category.Status", "");

            try
            {
                using (var doc = JsonDocument.Parse(jsonValue))
                {
                    var root = doc.RootElement;

                    // 1. Extract categorySucceeded — the authoritative outcome signal
                    bool? categorySucceeded = SafeGetBool(root, "categorySucceeded");
                    string categoryStatusMessage = SafeGetString(root, "categoryStatusMessage");

                    // 2. Parse subcategories — handles both flat strings and nested objects
                    var subcategories = ParseSubcategories(root);

                    // 3. Derive a meaningful status summary
                    string statusText;
                    if (categoryStatusMessage != null)
                        statusText = categoryStatusMessage;
                    else if (categorySucceeded == true)
                        statusText = "Complete";
                    else if (categorySucceeded == false)
                        statusText = "Failed";
                    else
                        statusText = BuildProgressSummary(subcategories);

                    // 4. Decide whether to emit an event
                    bool isFirstSeen = !_provisioningCategorySeen.Contains(categoryName);
                    bool categorySucceededChanged = HasCategorySucceededChanged(categoryName, categorySucceeded);

                    if (isFirstSeen)
                    {
                        _provisioningCategorySeen.Add(categoryName);
                        _lastCategorySucceeded[categoryName] = categorySucceeded;
                        EmitProvisioningEvent(categoryLabel, categorySucceeded, statusText, subcategories, EventSeverity.Info);
                    }
                    else if (categorySucceededChanged)
                    {
                        _lastCategorySucceeded[categoryName] = categorySucceeded;
                        var severity = categorySucceeded == false ? EventSeverity.Warning : EventSeverity.Info;
                        EmitProvisioningEvent(categoryLabel, categorySucceeded, statusText, subcategories, severity);
                    }
                    // else: intermediate subcategory text change — log only, no event

                    // 5. Track resolved categories (both success and failure are final)
                    if (categorySucceeded.HasValue)
                        _provisioningCategoriesResolved.Add(categoryName);

                    // 6. Handle failure
                    if (categorySucceeded == false)
                    {
                        if (_provisioningFailureFired.Contains(categoryName))
                            return ProvisioningResult.NoAction;

                        _provisioningFailureFired.Add(categoryName);

                        var failedSubcategory = FindFailedSubcategory(subcategories);
                        var failureTypeName = failedSubcategory != null
                            ? $"Provisioning_{categoryLabel}_{failedSubcategory}_Failed"
                            : $"Provisioning_{categoryLabel}_Failed";

                        _logger.Warning($"Provisioning failure detected: {failureTypeName}");
                        return ProvisioningResult.Failure(failureTypeName);
                    }

                    return ProvisioningResult.NoAction;
                }
            }
            catch (JsonException ex)
            {
                _logger.Warning($"Failed to parse provisioning status JSON for {categoryLabel}: {ex.Message}");
                return ProvisioningResult.NoAction;
            }
            catch (Exception ex)
            {
                _logger.Warning($"Unexpected error processing provisioning status for {categoryLabel}: {ex.Message}");
                return ProvisioningResult.NoAction;
            }
        }

        /// <summary>
        /// Checks whether categorySucceeded has transitioned from null to true/false.
        /// This ensures we only emit one event when the outcome is decided.
        /// </summary>
        private bool HasCategorySucceededChanged(string categoryName, bool? newValue)
        {
            if (!_lastCategorySucceeded.TryGetValue(categoryName, out var oldValue))
                return false; // First-seen is handled separately

            // Transition from null (in-progress) to true/false (resolved)
            return oldValue != newValue;
        }

        private void EmitProvisioningEvent(string categoryLabel, bool? succeeded, string statusText,
            List<SubcategoryInfo> subcategories, EventSeverity severity)
        {
            var eventData = new Dictionary<string, object>
            {
                { "category", categoryLabel },
                { "categorySucceeded", succeeded?.ToString() ?? "in_progress" },
                { "categoryStatusMessage", statusText }
            };

            if (subcategories.Count > 0)
            {
                var subcatData = new Dictionary<string, object>();
                foreach (var sub in subcategories)
                {
                    subcatData[sub.Name] = new Dictionary<string, string>
                    {
                        { "state", sub.State },
                        { "statusText", sub.StatusText }
                    };
                }
                eventData["subcategories"] = subcatData;
            }

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

            _logger.Info($"Provisioning status event: {categoryLabel} — {statusText} (succeeded={succeeded?.ToString() ?? "in_progress"})");
        }

        // ===== JSON Parsing Helpers =====

        /// <summary>
        /// Parses subcategory entries from the JSON. Handles both formats:
        ///   Flat:   "CertificatesSubcategory": "Certificates (1 of 1 applied)"
        ///   Nested: "CertificatesSubcategory": { "subcategoryState": "succeeded", "subcategoryStatusText": "..." }
        /// </summary>
        private static List<SubcategoryInfo> ParseSubcategories(JsonElement root)
        {
            var result = new List<SubcategoryInfo>();

            foreach (var prop in root.EnumerateObject())
            {
                if (!prop.Name.Contains("Subcategory", StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = CleanSubcategoryName(prop.Name);

                switch (prop.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        // Flat format: value is the status text, derive state from text content
                        var text = prop.Value.GetString() ?? "";
                        result.Add(new SubcategoryInfo
                        {
                            Name = name,
                            State = InferStateFromText(text),
                            StatusText = text
                        });
                        break;

                    case JsonValueKind.Object:
                        // Nested format: { "subcategoryState": "...", "subcategoryStatusText": "..." }
                        result.Add(new SubcategoryInfo
                        {
                            Name = name,
                            State = SafeGetString(prop.Value, "subcategoryState") ?? "unknown",
                            StatusText = SafeGetString(prop.Value, "subcategoryStatusText") ?? ""
                        });
                        break;

                    default:
                        // Unknown format — include with raw value for debugging
                        result.Add(new SubcategoryInfo
                        {
                            Name = name,
                            State = "unknown",
                            StatusText = prop.Value.ToString()
                        });
                        break;
                }
            }

            return result;
        }

        /// <summary>
        /// Cleans a subcategory property name to a readable short name.
        /// "AccountSetup.CertificatesSubcategory" -> "Certificates"
        /// "SecurityPoliciesSubcategory" -> "SecurityPolicies"
        /// </summary>
        private static string CleanSubcategoryName(string rawName)
        {
            var name = rawName;

            // Remove "Subcategory" suffix
            var idx = name.IndexOf("Subcategory", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                name = name.Substring(0, idx);

            // Remove category prefix (e.g. "AccountSetup.", "DeviceSetup.", "DevicePreparation.")
            var dotIdx = name.LastIndexOf('.');
            if (dotIdx >= 0 && dotIdx < name.Length - 1)
                name = name.Substring(dotIdx + 1);

            return string.IsNullOrEmpty(name) ? rawName : name;
        }

        /// <summary>
        /// Infers a state string from flat-format subcategory text.
        /// Used when the JSON doesn't have explicit subcategoryState.
        /// </summary>
        private static string InferStateFromText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "unknown";

            if (text.Contains("Complete", StringComparison.OrdinalIgnoreCase)
                || text.Contains("applied", StringComparison.OrdinalIgnoreCase)
                || text.Contains("installed", StringComparison.OrdinalIgnoreCase)
                || text.Contains("added", StringComparison.OrdinalIgnoreCase)
                || text.Contains("No setup needed", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Identified", StringComparison.OrdinalIgnoreCase))
            {
                return "succeeded";
            }

            if (text.Contains("Error", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Failed", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Failure", StringComparison.OrdinalIgnoreCase))
            {
                return "failed";
            }

            return "in_progress";
        }

        /// <summary>
        /// Finds the first subcategory that is not in a succeeded state.
        /// Returns the clean name, or null if all succeeded (generic failure).
        /// </summary>
        private static string FindFailedSubcategory(List<SubcategoryInfo> subcategories)
        {
            foreach (var sub in subcategories)
            {
                if (!string.Equals(sub.State, "succeeded", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(sub.State, "notRequired", StringComparison.OrdinalIgnoreCase))
                {
                    return sub.Name;
                }
            }

            return null;
        }

        /// <summary>
        /// Builds a human-readable progress summary from subcategory states.
        /// E.g. "3 of 5 subcategories completed"
        /// </summary>
        private static string BuildProgressSummary(List<SubcategoryInfo> subcategories)
        {
            if (subcategories.Count == 0)
                return "In progress";

            var succeeded = subcategories.Count(s =>
                string.Equals(s.State, "succeeded", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.State, "notRequired", StringComparison.OrdinalIgnoreCase));

            var failed = subcategories.Count(s =>
                string.Equals(s.State, "failed", StringComparison.OrdinalIgnoreCase));

            if (failed > 0)
                return $"{failed} of {subcategories.Count} subcategories failed";

            return $"{succeeded} of {subcategories.Count} subcategories completed";
        }

        /// <summary>
        /// Safely extracts a boolean from a JSON property. Handles True, False, and string "true"/"false".
        /// Returns null if the property doesn't exist or has an unexpected type.
        /// </summary>
        private static bool? SafeGetBool(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var prop))
                return null;

            switch (prop.ValueKind)
            {
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.String:
                    return bool.TryParse(prop.GetString(), out var parsed) ? parsed : (bool?)null;
                default: return null;
            }
        }

        /// <summary>
        /// Safely extracts a string from a JSON property. Returns null if missing or not a string.
        /// </summary>
        private static string SafeGetString(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var prop))
                return null;

            return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
        }

        // ===== Internal Types =====

        private class SubcategoryInfo
        {
            public string Name { get; set; }
            public string State { get; set; }      // "succeeded", "failed", "in_progress", "unknown", "notRequired"
            public string StatusText { get; set; }  // Human-readable text
        }

        private readonly struct ProvisioningResult
        {
            public readonly bool IsFailed;
            public readonly string FailureType;

            private ProvisioningResult(bool isFailed, string failureType)
            {
                IsFailed = isFailed;
                FailureType = failureType;
            }

            public static ProvisioningResult NoAction => new ProvisioningResult(false, null);
            public static ProvisioningResult Failure(string failureType) => new ProvisioningResult(true, failureType);
        }
    }
}
