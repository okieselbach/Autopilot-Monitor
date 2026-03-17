using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using AutopilotMonitor.Agent.Core.Monitoring.Core;
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
    ///   - Emit on every JSON change (progress updates like "Apps 1/5 → 2/5", state transitions)
    ///   - Emit once when categorySucceeded resolves to true or false (final outcome)
    ///   - Fire EspFailureDetected on subcategory failure even if categorySucceeded is still null
    ///
    /// Uses RegistryWatcher (RegNotifyChangeKeyValue) for instant registry change detection.
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
        // Fire-once guard for DeviceSetupProvisioningComplete event
        private bool _deviceSetupProvisioningCompleteFired;
        // Fallback: timer for when all subcategories succeeded but categorySucceeded is still null.
        // Some Windows builds (e.g. 25H2/26200) never set the categorySucceeded boolean.
        private Timer _deviceSetupFallbackTimer;
        private const int DeviceSetupFallbackDelaySeconds = 30;

        private void StartProvisioningStatusWatcher()
        {
            _lastProvisioningJson = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _provisioningCategorySeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _lastCategorySucceeded = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
            _provisioningFailureFired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _provisioningCategoriesResolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _lastSubcategoryStates = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            _deviceSetupProvisioningCompleteFired = false;
            _deviceSetupFallbackTimer = null;

            // Try to start immediately; if key doesn't exist yet, retry every 2s
            if (!TryStartProvisioningWatcher())
            {
                _logger.Info("Provisioning status registry key not yet present — retrying every 2s");
                _provisioningWatcherRetryTimer = new System.Threading.Timer(
                    _ =>
                    {
                        if (TryStartProvisioningWatcher())
                        {
                            _provisioningWatcherRetryTimer?.Dispose();
                            _provisioningWatcherRetryTimer = null;
                        }
                    },
                    null,
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(2));
            }
        }

        /// <summary>
        /// Attempts to create and start the RegistryWatcher for the provisioning key.
        /// Returns false if the key doesn't exist yet.
        /// </summary>
        private bool TryStartProvisioningWatcher()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(ProvisioningStatusRegistryPath))
                {
                    if (key == null)
                        return false;
                }

                // Capture initial state before watcher starts
                _logger.Trace("ProvisioningWatcher: capturing initial state before watcher starts");
                CheckProvisioningStatus();

                // Create and start watcher
                _provisioningWatcher = new RegistryWatcher(
                    RegistryHive.LocalMachine,
                    ProvisioningStatusRegistryPath,
                    watchSubtree: true,
                    filter: RegistryNativeMethods.RegChangeNotifyFilter.LastSet,
                    trace: msg => _logger.Trace($"RegistryWatcher: {msg}"));

                _provisioningWatcher.Changed += (s, e) =>
                {
                    _logger.Trace("ProvisioningWatcher: Changed event fired — calling CheckProvisioningStatus");
                    CheckProvisioningStatus();
                };
                _provisioningWatcher.Error += (s, ex) => _logger.Warning($"Provisioning watcher handler error: {ex.Message}");

                _provisioningWatcher.Start();
                _logger.Info("Provisioning status registry watcher started");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to start provisioning watcher: {ex.Message}");
                return false;
            }
        }

        private void StopProvisioningStatusWatcher(string reason)
        {
            try
            {
                _provisioningWatcherRetryTimer?.Dispose();
                _provisioningWatcherRetryTimer = null;

                _deviceSetupFallbackTimer?.Dispose();
                _deviceSetupFallbackTimer = null;

                if (_provisioningWatcher != null)
                {
                    _provisioningWatcher.Dispose();
                    _provisioningWatcher = null;
                    _logger.Info($"Provisioning status watcher stopped: {reason}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Error stopping provisioning status watcher", ex);
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
                        {
                            _logger.Trace("CheckProvisioningStatus: registry key not found");
                            return;
                        }

                        bool failureDetected = false;
                        string failureType = null;
                        int changedCount = 0;
                        int unchangedCount = 0;
                        int missingCount = 0;

                        foreach (var categoryName in ProvisioningCategoryNames)
                        {
                            var jsonValue = key.GetValue(categoryName)?.ToString();
                            if (string.IsNullOrEmpty(jsonValue))
                            {
                                missingCount++;
                                _logger.Trace($"CheckProvisioningStatus: {categoryName} — not present in registry");
                                continue;
                            }

                            // Skip if JSON is identical to last check (no change at all)
                            if (_lastProvisioningJson.TryGetValue(categoryName, out var lastJson) && lastJson == jsonValue)
                            {
                                unchangedCount++;
                                continue;
                            }

                            changedCount++;
                            bool isNew = !_lastProvisioningJson.ContainsKey(categoryName);
                            _logger.Trace($"CheckProvisioningStatus: {categoryName} — {(isNew ? "NEW" : "CHANGED")} (json length={jsonValue.Length})");
                            _logger.Trace($"CheckProvisioningStatus: {categoryName} — raw JSON: {jsonValue}");

                            _lastProvisioningJson[categoryName] = jsonValue;
                            var result = ProcessCategoryStatus(categoryName, jsonValue);

                            if (result.IsFailed)
                            {
                                failureDetected = true;
                                failureType = result.FailureType;
                            }
                        }

                        _logger.Trace($"CheckProvisioningStatus: summary — changed={changedCount}, unchanged={unchangedCount}, missing={missingCount}, " +
                                     $"seen={_provisioningCategorySeen.Count}, resolved={_provisioningCategoriesResolved.Count}/{_lastProvisioningJson.Count}, " +
                                     $"failuresFired={_provisioningFailureFired.Count}");

                        // Self-termination: only auto-stop when all categories resolved with success (no failures).
                        // Uses RequestStop() which is safe to call from within the Changed handler (non-blocking).
                        if (_provisioningCategoriesResolved.Count > 0
                            && _lastProvisioningJson.Count > 0
                            && _provisioningCategoriesResolved.Count >= _lastProvisioningJson.Count
                            && !_provisioningFailureFired.Any())
                        {
                            _logger.Info("Provisioning status watcher: all categories resolved with success — requesting stop");
                            _provisioningWatcher?.RequestStop();
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

                        // Fire DeviceSetupProvisioningComplete when DeviceSetup category resolves with success.
                        // Used as completion signal for Self-Deploying mode where Shell-Core ESP exit
                        // and desktop arrival may never arrive.
                        if (!_deviceSetupProvisioningCompleteFired
                            && _lastCategorySucceeded.TryGetValue("DeviceSetupCategory.Status", out var dsSucceeded)
                            && dsSucceeded == true)
                        {
                            _deviceSetupProvisioningCompleteFired = true;
                            _logger.Info("Provisioning status: DeviceSetup succeeded — firing DeviceSetupProvisioningComplete");
                            try
                            {
                                DeviceSetupProvisioningComplete?.Invoke(this, EventArgs.Empty);
                            }
                            catch (Exception ex)
                            {
                                _logger.Error("DeviceSetupProvisioningComplete handler failed", ex);
                            }
                        }

                        // Fallback: All subcategories succeeded but categorySucceeded was never set by Windows.
                        // Some Windows builds (e.g. 25H2/26200) don't set the boolean even after all subcategories succeed.
                        // Wait DeviceSetupFallbackDelaySeconds to give Windows time, then treat as success if still consistent.
                        if (!_deviceSetupProvisioningCompleteFired
                            && _deviceSetupFallbackTimer == null
                            && _lastCategorySucceeded.TryGetValue("DeviceSetupCategory.Status", out var dsFallbackState)
                            && dsFallbackState == null
                            && _lastSubcategoryStates.TryGetValue("DeviceSetupCategory.Status", out var dsFallbackSubStates)
                            && dsFallbackSubStates.Count > 0
                            && dsFallbackSubStates.Values.All(s =>
                                string.Equals(s, "succeeded", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(s, "notRequired", StringComparison.OrdinalIgnoreCase)))
                        {
                            _logger.Info($"Provisioning status: DeviceSetup — all {dsFallbackSubStates.Count} subcategories succeeded " +
                                         $"but categorySucceeded not set by Windows — starting {DeviceSetupFallbackDelaySeconds}s fallback timer");
                            _deviceSetupFallbackTimer = new Timer(
                                OnDeviceSetupFallbackTimerExpired,
                                null,
                                TimeSpan.FromSeconds(DeviceSetupFallbackDelaySeconds),
                                Timeout.InfiniteTimeSpan);
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
        /// Fallback timer callback: fires after DeviceSetupFallbackDelaySeconds when all subcategories
        /// succeeded but categorySucceeded was never set by Windows. Re-reads registry to confirm,
        /// emits a visible WARNING event for the admin timeline, then fires DeviceSetupProvisioningComplete.
        /// </summary>
        private void OnDeviceSetupFallbackTimerExpired(object state)
        {
            try
            {
                lock (_stateLock)
                {
                    if (_deviceSetupProvisioningCompleteFired)
                    {
                        _logger.Debug("DeviceSetup fallback timer expired but DeviceSetupProvisioningComplete already fired — ignoring");
                        return;
                    }

                    // Re-read registry to confirm state is still consistent
                    string registryJson = null;
                    using (var key = Registry.LocalMachine.OpenSubKey(ProvisioningStatusRegistryPath, writable: false))
                    {
                        registryJson = key?.GetValue("DeviceSetupCategory.Status")?.ToString();
                    }

                    if (string.IsNullOrEmpty(registryJson))
                    {
                        _logger.Warning("DeviceSetup fallback timer: registry value not found — aborting fallback");
                        return;
                    }

                    // Parse and verify: categorySucceeded still null, all subcategories still succeeded
                    bool? categorySucceeded = null;
                    List<SubcategoryInfo> subcategories = null;
                    try
                    {
                        using (var doc = JsonDocument.Parse(registryJson))
                        {
                            categorySucceeded = SafeGetBool(doc.RootElement, "categorySucceeded");
                            subcategories = ParseSubcategories(doc.RootElement);
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.Warning($"DeviceSetup fallback timer: failed to parse registry JSON — aborting fallback: {ex.Message}");
                        return;
                    }

                    // If categorySucceeded was set in the meantime, let the normal path handle it
                    if (categorySucceeded.HasValue)
                    {
                        _logger.Info($"DeviceSetup fallback timer: categorySucceeded is now {categorySucceeded.Value} — normal path will handle this");
                        // Trigger a re-check so the normal path picks up the change
                        CheckProvisioningStatus();
                        return;
                    }

                    // Verify all subcategories are still succeeded/notRequired
                    if (subcategories == null || subcategories.Count == 0)
                    {
                        _logger.Warning("DeviceSetup fallback timer: no subcategories found — aborting fallback");
                        return;
                    }

                    var nonSucceeded = subcategories.Where(s =>
                        !string.Equals(s.State, "succeeded", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(s.State, "notRequired", StringComparison.OrdinalIgnoreCase)).ToList();

                    if (nonSucceeded.Count > 0)
                    {
                        _logger.Warning($"DeviceSetup fallback timer: {nonSucceeded.Count} subcategory/ies not succeeded " +
                                        $"({string.Join(", ", nonSucceeded.Select(s => $"{s.Name}={s.State}"))}) — aborting fallback");
                        return;
                    }

                    // All conditions confirmed after delay — dump raw JSON and emit visible WARNING event
                    EmitRawRegistryDump("DeviceSetup", registryJson, "fallback_confirmed");

                    _logger.Warning($"DeviceSetup fallback: all {subcategories.Count} subcategories succeeded but " +
                                    $"categorySucceeded was not set by Windows after {DeviceSetupFallbackDelaySeconds}s — treating as complete");

                    var subcatData = new Dictionary<string, object>();
                    foreach (var sub in subcategories)
                    {
                        subcatData[sub.Name] = new Dictionary<string, string>
                        {
                            { "state", sub.State },
                            { "statusText", sub.StatusText }
                        };
                    }

                    _onEventCollected(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        EventType = Constants.EventTypes.EspProvisioningStatus,
                        Severity = EventSeverity.Warning,
                        Source = "EspAndHelloTracker",
                        Phase = EnrollmentPhase.Unknown,
                        Message = $"ESP provisioning status: DeviceSetup — all {subcategories.Count} subcategories succeeded " +
                                  $"but categorySucceeded was not confirmed by Windows — treating as complete (fallback after {DeviceSetupFallbackDelaySeconds}s)",
                        Data = new Dictionary<string, object>
                        {
                            { "category", "DeviceSetup" },
                            { "categorySucceeded", "in_progress" },
                            { "fallbackApplied", true },
                            { "fallbackReason", "all_subcategories_succeeded_category_unresolved" },
                            { "fallbackDelaySeconds", DeviceSetupFallbackDelaySeconds },
                            { "subcategoryCount", subcategories.Count },
                            { "subcategories", subcatData }
                        }
                    });

                    _deviceSetupProvisioningCompleteFired = true;
                    try
                    {
                        DeviceSetupProvisioningComplete?.Invoke(this, EventArgs.Empty);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("DeviceSetupProvisioningComplete handler failed (fallback path)", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"DeviceSetup fallback timer callback failed: {ex.Message}", ex);
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

                    _logger.Trace($"ProcessCategory: {categoryLabel} — succeeded={categorySucceeded?.ToString() ?? "null"}, " +
                                 $"isFirstSeen={isFirstSeen}, succeededChanged={categorySucceededChanged}, " +
                                 $"subcategories={subcategories.Count} [{string.Join(", ", subcategories.Select(s => $"{s.Name}={s.State}"))}]");

                    if (isFirstSeen)
                    {
                        _logger.Trace($"ProcessCategory: {categoryLabel} — EMITTING first-seen event");
                        _provisioningCategorySeen.Add(categoryName);
                        _lastCategorySucceeded[categoryName] = categorySucceeded;
                        StoreSubcategoryStates(categoryName, subcategories);
                        EmitProvisioningEvent(categoryLabel, categorySucceeded, statusText, subcategories, EventSeverity.Info);
                        EmitRawRegistryDump(categoryLabel, jsonValue, "first_seen");
                    }
                    else if (categorySucceededChanged)
                    {
                        _logger.Trace($"ProcessCategory: {categoryLabel} — EMITTING categorySucceeded change ({_lastCategorySucceeded[categoryName]?.ToString() ?? "null"} → {categorySucceeded?.ToString() ?? "null"})");
                        _lastCategorySucceeded[categoryName] = categorySucceeded;
                        StoreSubcategoryStates(categoryName, subcategories);
                        var severity = categorySucceeded == false ? EventSeverity.Warning : EventSeverity.Info;
                        EmitProvisioningEvent(categoryLabel, categorySucceeded, statusText, subcategories, severity);
                        EmitRawRegistryDump(categoryLabel, jsonValue, $"category_resolved_{(categorySucceeded == true ? "success" : "failed")}");
                    }
                    else
                    {
                        // Emit on every JSON change — the registry dedup ensures we only reach here
                        // when actual progress happened (e.g. "Apps 1/5 → 2/5", subcategory completions).
                        var transitions = DetectSubcategoryTransitions(categoryName, subcategories);
                        StoreSubcategoryStates(categoryName, subcategories);

                        var failureTransitions = transitions.Where(t => t.IsFailure).ToList();
                        if (transitions.Count > 0)
                        {
                            _logger.Trace($"ProcessCategory: {categoryLabel} — subcategory transitions: " +
                                         string.Join(", ", transitions.Select(t => $"{t.SubcategoryName}: {t.OldState}→{t.NewState} (failure={t.IsFailure})")));
                        }

                        if (failureTransitions.Count > 0)
                        {
                            _logger.Trace($"ProcessCategory: {categoryLabel} — EMITTING failure transition event");
                            EmitProvisioningEvent(categoryLabel, categorySucceeded, statusText, subcategories, EventSeverity.Warning, failureTransitions);
                        }
                        else if (transitions.Count > 0)
                        {
                            _logger.Trace($"ProcessCategory: {categoryLabel} — EMITTING subcategory transition event");
                            EmitProvisioningEvent(categoryLabel, categorySucceeded, statusText, subcategories, EventSeverity.Info, transitions);
                        }
                        else
                        {
                            // JSON changed but no state transitions — progress text changed (e.g. "Apps 1/5 → 2/5")
                            _logger.Trace($"ProcessCategory: {categoryLabel} — EMITTING progress update (JSON changed, no state transitions)");
                            EmitProvisioningEvent(categoryLabel, categorySucceeded, statusText, subcategories, EventSeverity.Info);
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

            _logger.Info($"Provisioning subcategory failure: {categoryLabel}/{failedSubcategory ?? "category-level"} — escalating as {failureTypeName}");
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

        /// <summary>
        /// Emits the raw registry JSON as a Trace-level event for post-mortem format debugging.
        /// Called at first-seen (initial snapshot) and at resolution (categorySucceeded change or fallback)
        /// so we capture the registry format at the start and at the critical decision point.
        /// </summary>
        private void EmitRawRegistryDump(string categoryLabel, string rawJson, string trigger)
        {
            _onEventCollected(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "esp_provisioning_raw",
                Severity = EventSeverity.Trace,
                Source = "EspAndHelloTracker",
                Phase = EnrollmentPhase.Unknown,
                Message = $"ESP provisioning raw registry: {categoryLabel} ({trigger})",
                Data = new Dictionary<string, object>
                {
                    { "category", categoryLabel },
                    { "trigger", trigger },
                    { "registryValue", $"{categoryLabel}Category.Status" },
                    { "rawJson", rawJson }
                }
            });
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
