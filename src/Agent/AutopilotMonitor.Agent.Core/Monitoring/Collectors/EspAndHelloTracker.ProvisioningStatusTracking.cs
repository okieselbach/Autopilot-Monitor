using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using AutopilotMonitor.Agent.Core.Monitoring.Interop;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.Core.Monitoring.Collectors
{
    /// <summary>
    /// Partial: Watches ESP provisioning category status from registry to detect failures
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
    ///   - Emit when subcategory states change meaningfully (in_progress->succeeded, any->failed)
    ///   - Emit once when categorySucceeded resolves to true or false (final outcome)
    ///   - Fire EspFailureDetected on subcategory failure even if categorySucceeded is still null
    ///
    /// Uses RegNotifyChangeKeyValue for instant registry change detection (no polling timer).
    /// </summary>
    public partial class EspAndHelloTracker
    {
        private const string ProvisioningStatusRegistryPath = @"SOFTWARE\Microsoft\Provisioning\AutopilotSettings";

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
        // Track subcategory states per category — detect meaningful state transitions
        private Dictionary<string, Dictionary<string, string>> _lastSubcategoryStates;

        private void StartProvisioningStatusWatcher()
        {
            _lastProvisioningJson = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _provisioningCategorySeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _lastCategorySucceeded = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
            _provisioningFailureFired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _provisioningCategoriesResolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _lastSubcategoryStates = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            _registryWatcherStopEvent = RegistryWatcherNativeMethods.CreateEvent(IntPtr.Zero, true, false, null);
            if (_registryWatcherStopEvent == IntPtr.Zero)
            {
                _logger.Warning("Failed to create registry watcher stop event — provisioning status tracking disabled");
                return;
            }

            _registryWatcherThread = new Thread(RegistryWatcherLoop)
            {
                IsBackground = true,
                Name = "ProvisioningStatusWatcher"
            };
            _registryWatcherThread.Start();

            _logger.Info("Provisioning status registry watcher started");
        }

        private void StopProvisioningStatusWatcher(string reason)
        {
            if (_registryWatcherStopEvent == IntPtr.Zero)
                return;

            try
            {
                // Signal stop event to wake up the watcher thread
                RegistryWatcherNativeMethods.SetEvent(_registryWatcherStopEvent);

                // Wait for thread to exit
                if (_registryWatcherThread != null && _registryWatcherThread.IsAlive)
                {
                    _registryWatcherThread.Join(TimeSpan.FromSeconds(3));
                }

                RegistryWatcherNativeMethods.CloseHandle(_registryWatcherStopEvent);
                _registryWatcherStopEvent = IntPtr.Zero;
                _registryWatcherThread = null;

                _logger.Info($"Provisioning status watcher stopped: {reason}");
            }
            catch (Exception ex)
            {
                _logger.Error("Error stopping provisioning status watcher", ex);
            }
        }

        /// <summary>
        /// Background thread: opens the provisioning registry key and waits for value changes
        /// via RegNotifyChangeKeyValue. Falls back to retry loop if key doesn't exist yet.
        /// </summary>
        private void RegistryWatcherLoop()
        {
            IntPtr hKey = IntPtr.Zero;
            IntPtr hNotifyEvent = IntPtr.Zero;

            try
            {
                // Wait for registry key to appear (enrollment may not have started yet)
                while (true)
                {
                    int result = RegistryWatcherNativeMethods.RegOpenKeyEx(
                        RegistryWatcherNativeMethods.HKEY_LOCAL_MACHINE,
                        ProvisioningStatusRegistryPath,
                        0,
                        RegistryWatcherNativeMethods.KEY_NOTIFY | RegistryWatcherNativeMethods.KEY_READ,
                        out hKey);

                    if (result == 0)
                        break;

                    // Key doesn't exist yet — wait 2 seconds or until stop signaled
                    uint waitResult = RegistryWatcherNativeMethods.WaitForSingleObject(_registryWatcherStopEvent, 2000);
                    if (waitResult == RegistryWatcherNativeMethods.WAIT_OBJECT_0)
                        return; // Stop requested
                }

                // Capture initial state before notification loop.
                // Any changes between this read and the first RegNotifyChangeKeyValue registration
                // are covered by the 15s heartbeat fallback.
                CheckProvisioningStatus();

                // Create notification event
                hNotifyEvent = RegistryWatcherNativeMethods.CreateEvent(IntPtr.Zero, false, false, null);
                if (hNotifyEvent == IntPtr.Zero)
                {
                    _logger.Warning("Failed to create registry notify event — watcher thread exiting");
                    return;
                }

                var waitHandles = new[] { hNotifyEvent, _registryWatcherStopEvent };

                // Main watch loop: register → wait → read
                // IMPORTANT: CheckProvisioningStatus() must run AFTER WaitForMultipleObjects, not between
                // RegNotifyChangeKeyValue and Wait. The managed Registry.OpenSubKey() call interferes with
                // pending RegNotifyChangeKeyValue notifications when called before Wait (proven in Build 416).
                // The 15s heartbeat ensures changes are detected even if a notification is missed.
                const uint heartbeatMs = 15_000;

                while (true)
                {
                    // 1. Register for value change notifications
                    int regResult = RegistryWatcherNativeMethods.RegNotifyChangeKeyValue(
                        hKey,
                        true,  // Watch subtree
                        RegistryWatcherNativeMethods.REG_NOTIFY_CHANGE_LAST_SET,
                        hNotifyEvent,
                        true); // Asynchronous

                    if (regResult != 0)
                    {
                        _logger.Warning($"RegNotifyChangeKeyValue failed (error {regResult}) — watcher thread exiting");
                        return;
                    }

                    // 2. Wait for registry change notification or heartbeat timeout
                    uint waitResult = RegistryWatcherNativeMethods.WaitForMultipleObjects(
                        (uint)waitHandles.Length,
                        waitHandles,
                        false,
                        heartbeatMs);

                    if (waitResult == RegistryWatcherNativeMethods.WAIT_OBJECT_0)
                    {
                        // Registry notification fired — instant detection
                        _logger.Debug("Provisioning status watcher: registry change notification received");
                    }
                    else if (waitResult == RegistryWatcherNativeMethods.WAIT_OBJECT_0 + 1)
                    {
                        return; // Stop requested
                    }
                    else if (waitResult == RegistryWatcherNativeMethods.WAIT_TIMEOUT)
                    {
                        // Heartbeat — re-read to catch any missed notifications
                    }
                    else
                    {
                        _logger.Warning($"Registry watcher unexpected wait result: {waitResult} — exiting");
                        return;
                    }

                    // 3. Read current state AFTER wait (not between register and wait)
                    CheckProvisioningStatus();
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Registry watcher thread failed: {ex.Message}");
            }
            finally
            {
                if (hNotifyEvent != IntPtr.Zero)
                    RegistryWatcherNativeMethods.CloseHandle(hNotifyEvent);
                if (hKey != IntPtr.Zero)
                    RegistryWatcherNativeMethods.RegCloseKey(hKey);
            }
        }

        private void CheckProvisioningStatus()
        {
            try
            {
                lock (_stateLock)
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(ProvisioningStatusRegistryPath, writable: false))
                    {
                        if (key == null)
                            return;

                        bool failureDetected = false;
                        string failureType = null;

                        foreach (var categoryName in ProvisioningCategoryNames)
                        {
                            var jsonValue = key.GetValue(categoryName)?.ToString();
                            if (string.IsNullOrEmpty(jsonValue))
                                continue;

                            // Skip if JSON is identical to last check (no change at all)
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

                        // Self-termination: only auto-stop when all categories resolved with success (no failures)
                        // Signal the stop event directly (we're on the watcher thread — can't call StopProvisioningStatusWatcher
                        // which would Join() on ourselves)
                        if (_provisioningCategoriesResolved.Count > 0
                            && _lastProvisioningJson.Count > 0
                            && _provisioningCategoriesResolved.Count >= _lastProvisioningJson.Count
                            && !_provisioningFailureFired.Any())
                        {
                            _logger.Info("Provisioning status watcher: all categories resolved with success — signaling stop");
                            RegistryWatcherNativeMethods.SetEvent(_registryWatcherStopEvent);
                        }

                        // Fire EspFailureDetected AFTER event emission
                        // (matches the pattern in ShellCoreTracking.cs — event in spool before agent reacts)
                        if (failureDetected && failureType != null)
                        {
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
            }
            catch (Exception ex)
            {
                _logger.Warning($"Provisioning status check failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Processes a single category status JSON value.
        /// Emits events on first-seen, subcategory state transitions, and categorySucceeded resolution.
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
                        StoreSubcategoryStates(categoryName, subcategories);
                        EmitProvisioningEvent(categoryLabel, categorySucceeded, statusText, subcategories, EventSeverity.Info);
                    }
                    else if (categorySucceededChanged)
                    {
                        _lastCategorySucceeded[categoryName] = categorySucceeded;
                        StoreSubcategoryStates(categoryName, subcategories);
                        var severity = categorySucceeded == false ? EventSeverity.Warning : EventSeverity.Info;
                        EmitProvisioningEvent(categoryLabel, categorySucceeded, statusText, subcategories, severity);
                    }
                    else
                    {
                        // Check for subcategory failure transitions (only failures trigger events — success transitions are expected noise)
                        var transitions = DetectSubcategoryTransitions(categoryName, subcategories);
                        StoreSubcategoryStates(categoryName, subcategories);

                        var failureTransitions = transitions.Where(t => t.IsFailure).ToList();
                        if (failureTransitions.Count > 0)
                        {
                            EmitProvisioningEvent(categoryLabel, categorySucceeded, statusText, subcategories, EventSeverity.Warning, failureTransitions);
                        }
                    }

                    // 5. Track resolved categories (both success and failure are final)
                    if (categorySucceeded.HasValue)
                        _provisioningCategoriesResolved.Add(categoryName);

                    // 6. Handle failure — either via categorySucceeded or subcategory state
                    if (categorySucceeded == false)
                    {
                        return TryFireProvisioningFailure(categoryName, categoryLabel, subcategories);
                    }

                    // 7. Check for subcategory-level failures even when categorySucceeded is still null
                    //    (catches timeout scenarios where the category never formally fails)
                    var failedSub = FindFailedSubcategory(subcategories);
                    if (failedSub != null && categorySucceeded == null)
                    {
                        return TryFireProvisioningFailure(categoryName, categoryLabel, subcategories);
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
        /// Attempts to fire EspFailureDetected for a provisioning failure. Uses fire-once guard.
        /// </summary>
        private ProvisioningResult TryFireProvisioningFailure(string categoryName, string categoryLabel, List<SubcategoryInfo> subcategories)
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

        /// <summary>
        /// Stores current subcategory states for later transition detection.
        /// </summary>
        private void StoreSubcategoryStates(string categoryName, List<SubcategoryInfo> subcategories)
        {
            var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sub in subcategories)
                states[sub.Name] = sub.State;
            _lastSubcategoryStates[categoryName] = states;
        }

        /// <summary>
        /// Detects meaningful subcategory state transitions by comparing current states to last known states.
        /// Meaningful transitions: any -> failed (Warning), in_progress -> succeeded (Info).
        /// NOT meaningful: notStarted -> in_progress (expected progression, noise).
        /// </summary>
        private List<SubcategoryTransition> DetectSubcategoryTransitions(string categoryName, List<SubcategoryInfo> subcategories)
        {
            var transitions = new List<SubcategoryTransition>();

            if (!_lastSubcategoryStates.TryGetValue(categoryName, out var lastStates))
                return transitions; // No previous states to compare

            foreach (var sub in subcategories)
            {
                if (!lastStates.TryGetValue(sub.Name, out var oldState))
                    continue; // New subcategory — skip (first-seen already emitted the snapshot)

                if (string.Equals(oldState, sub.State, StringComparison.OrdinalIgnoreCase))
                    continue; // No change

                bool isFailure = string.Equals(sub.State, "failed", StringComparison.OrdinalIgnoreCase);
                bool isCompletion = string.Equals(sub.State, "succeeded", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(oldState, "succeeded", StringComparison.OrdinalIgnoreCase);

                // Skip noise: notStarted -> in_progress is expected progression
                bool isNoise = string.Equals(oldState, "notStarted", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(sub.State, "in_progress", StringComparison.OrdinalIgnoreCase);

                if (isFailure || isCompletion)
                {
                    transitions.Add(new SubcategoryTransition(sub.Name, oldState, sub.State, isFailure));
                }
                else if (!isNoise)
                {
                    // Other non-noise transitions (e.g., unknown -> failed) — include if meaningful
                    if (isFailure)
                        transitions.Add(new SubcategoryTransition(sub.Name, oldState, sub.State, true));
                }
            }

            return transitions;
        }

        private void EmitProvisioningEvent(string categoryLabel, bool? succeeded, string statusText,
            List<SubcategoryInfo> subcategories, EventSeverity severity,
            List<SubcategoryTransition> transitions = null)
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

            if (transitions != null && transitions.Count > 0)
            {
                eventData["changeType"] = "subcategory_state_change";
                var transitionData = new List<Dictionary<string, string>>();
                foreach (var t in transitions)
                {
                    transitionData.Add(new Dictionary<string, string>
                    {
                        { "subcategory", t.SubcategoryName },
                        { "previousState", t.OldState },
                        { "newState", t.NewState }
                    });
                }
                eventData["transitions"] = transitionData;
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
        /// Finds the first subcategory that is in a failed state.
        /// Returns the clean name, or null if none are failed.
        /// </summary>
        private static string FindFailedSubcategory(List<SubcategoryInfo> subcategories)
        {
            foreach (var sub in subcategories)
            {
                if (string.Equals(sub.State, "failed", StringComparison.OrdinalIgnoreCase))
                    return sub.Name;
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

        private readonly struct SubcategoryTransition
        {
            public readonly string SubcategoryName;
            public readonly string OldState;
            public readonly string NewState;
            public readonly bool IsFailure;

            public SubcategoryTransition(string subcategoryName, string oldState, string newState, bool isFailure)
            {
                SubcategoryName = subcategoryName;
                OldState = oldState;
                NewState = newState;
                IsFailure = isFailure;
            }
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
