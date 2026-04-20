using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Flows;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Completion;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Watches ESP provisioning category status from the registry to detect failures
    /// that Shell-Core event 62407 patterns may miss (e.g. Certificate provisioning failures).
    ///
    /// Registry path: HKLM\SOFTWARE\Microsoft\Provisioning\AutopilotSettings
    /// Values: DevicePreparationCategory.Status, DeviceSetupCategory.Status, AccountSetupCategory.Status
    ///
    /// JSON format varies across Windows versions:
    ///   Flat:   { "CertificatesSubcategory": "Certificates (1 of 1 applied)", "categorySucceeded": true, ... }
    ///   Nested: { "CertificatesSubcategory": { "subcategoryState": "succeeded", "subcategoryStatusText": "..." }, ... }
    ///
    /// Event emission strategy:
    ///   - Emit once when a category first appears (initial snapshot)
    ///   - Emit on every JSON change (progress updates, state transitions)
    ///   - Emit once when categorySucceeded resolves to true/false (final outcome)
    ///   - Fire <see cref="EspFailureDetected"/> on subcategory failure even if categorySucceeded is null
    ///   - Fire <see cref="DeviceSetupProvisioningComplete"/> on DeviceSetup success (or fallback)
    ///
    /// Uses RegistryWatcher (RegNotifyChangeKeyValue) for instant registry change detection.
    /// </summary>
    internal sealed class ProvisioningStatusTracker : IDisposable
    {
        internal const string ProvisioningStatusRegistryPath = @"SOFTWARE\Microsoft\Provisioning\AutopilotSettings";
        internal const int DeviceSetupFallbackDelaySeconds = 30;
        internal const int ProvisioningDebounceMilliseconds = 1000;

        private static readonly string[] ProvisioningCategoryNames =
        {
            "DevicePreparationCategory.Status",
            "DeviceSetupCategory.Status",
            "AccountSetupCategory.Status"
        };

        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly Action<EnrollmentEvent> _onEventCollected;

        private RegistryWatcher _provisioningWatcher;
        private Timer _provisioningWatcherRetryTimer;
        private Timer _provisioningDebounceTimer;
        private Timer _deviceSetupFallbackTimer;

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
        // WhiteGlove confirmation: DeviceSetup registry contains SaveWhiteGloveSuccessResult=succeeded
        private bool _saveWhiteGloveSuccessResultSeen;
        // Track SaveWhiteGloveSuccessResult state transitions for observability (null → notStarted → succeeded)
        private string _lastSaveWhiteGloveState;
        private readonly object _stateLock = new object();

        public event EventHandler<string> EspFailureDetected;
        public event EventHandler DeviceSetupProvisioningComplete;

        public ProvisioningStatusTracker(
            string sessionId,
            string tenantId,
            Action<EnrollmentEvent> onEventCollected,
            AgentLogger logger)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _onEventCollected = onEventCollected ?? throw new ArgumentNullException(nameof(onEventCollected));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        // =====================================================================
        // Public snapshot / state API (forwarded by coordinator)
        // =====================================================================

        /// <summary>
        /// Snapshot of current provisioning-category state for consumers like the signal-correlated
        /// WhiteGlove detection. Thread-safe.
        /// </summary>
        public Dictionary<string, bool?> GetProvisioningCategorySnapshot()
        {
            lock (_stateLock)
            {
                return _lastCategorySucceeded == null
                    ? new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, bool?>(_lastCategorySucceeded, StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// True when DeviceSetupCategory.Status has resolved categorySucceeded=true OR when the
        /// fallback (all subcategories succeeded, categorySucceeded null) has been confirmed.
        /// </summary>
        public bool DeviceSetupCategorySucceeded
        {
            get
            {
                lock (_stateLock)
                {
                    if (_deviceSetupProvisioningCompleteFired)
                        return true;
                    return _lastCategorySucceeded != null
                        && _lastCategorySucceeded.TryGetValue("DeviceSetupCategory.Status", out var v)
                        && v == true;
                }
            }
        }

        /// <summary>
        /// True when any AccountSetup subcategory has been tracked (resolved or in progress).
        /// </summary>
        public bool HasAccountSetupActivity
        {
            get
            {
                lock (_stateLock)
                {
                    if (_lastSubcategoryStates == null)
                        return false;
                    return _lastSubcategoryStates.TryGetValue("AccountSetupCategory.Status", out var subs)
                        && subs != null
                        && subs.Count > 0;
                }
            }
        }

        /// <summary>
        /// True when DeviceSetup registry JSON contains a SaveWhiteGloveSuccessResult property
        /// with subcategoryState=succeeded. This is a definitive WhiteGlove (Pre-Provisioning)
        /// confirmation signal — Windows only writes this property during White Glove flows.
        /// </summary>
        public bool HasSaveWhiteGloveSuccessResult
        {
            get { lock (_stateLock) { return _saveWhiteGloveSuccessResultSeen; } }
        }

        /// <summary>
        /// Returns a thread-safe snapshot of the current ESP provisioning category status.
        /// All data is deep-copied under the lock so the caller can use it freely.
        /// Returns null if no provisioning data has been observed yet.
        /// </summary>
        public EspProvisioningSnapshot GetProvisioningSnapshot()
        {
            lock (_stateLock)
            {
                if (_provisioningCategorySeen == null || _provisioningCategorySeen.Count == 0)
                    return null;

                var outcomes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var cat in _provisioningCategorySeen)
                {
                    var label = cat.Replace("Category.Status", "");
                    if (_lastCategorySucceeded.TryGetValue(cat, out var succeeded))
                    {
                        outcomes[label] = succeeded == true ? "success"
                                        : succeeded == false ? "failed"
                                        : "in_progress";
                    }
                    else
                    {
                        outcomes[label] = "in_progress";
                    }
                }

                var subcats = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in _lastSubcategoryStates)
                {
                    var label = kvp.Key.Replace("Category.Status", "");
                    subcats[label] = new Dictionary<string, string>(kvp.Value, StringComparer.OrdinalIgnoreCase);
                }

                return new EspProvisioningSnapshot
                {
                    CategoryOutcomes = outcomes,
                    SubcategoryStates = subcats,
                    CategoriesSeen = _provisioningCategorySeen.Count,
                    CategoriesResolved = _provisioningCategoriesResolved.Count,
                    AllResolved = _provisioningCategoriesResolved.Count >= _provisioningCategorySeen.Count
                };
            }
        }

        // =====================================================================
        // Lifecycle
        // =====================================================================

        public void Start()
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
            if (!TryStartWatcher())
            {
                _logger.Info("Provisioning status registry key not yet present — retrying every 2s");
                _provisioningWatcherRetryTimer = new Timer(
                    _ =>
                    {
                        if (TryStartWatcher())
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

        public void Stop(string reason = "tracker_stopped")
        {
            try
            {
                _provisioningWatcherRetryTimer?.Dispose();
                _provisioningWatcherRetryTimer = null;

                _provisioningDebounceTimer?.Dispose();
                _provisioningDebounceTimer = null;

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

        public void Dispose() => Stop();

        private bool TryStartWatcher()
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

                // Debounce timer — coalesces rapid-fire registry notifications into a single check
                _provisioningDebounceTimer = new Timer(
                    _ => CheckProvisioningStatus(),
                    null,
                    Timeout.Infinite,
                    Timeout.Infinite);

                _provisioningWatcher = new RegistryWatcher(
                    RegistryHive.LocalMachine,
                    ProvisioningStatusRegistryPath,
                    watchSubtree: true,
                    filter: RegistryNativeMethods.RegChangeNotifyFilter.LastSet,
                    trace: msg => _logger.Trace($"RegistryWatcher: {msg}"));

                _provisioningWatcher.Changed += (s, e) =>
                {
                    _logger.Trace("ProvisioningWatcher: Changed event fired — debouncing CheckProvisioningStatus");
                    _provisioningDebounceTimer?.Change(ProvisioningDebounceMilliseconds, Timeout.Infinite);
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

        // =====================================================================
        // Registry polling + processing
        // =====================================================================

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
                        int changedCount = 0, unchangedCount = 0, missingCount = 0;

                        foreach (var categoryName in ProvisioningCategoryNames)
                        {
                            var jsonValue = key.GetValue(categoryName)?.ToString();
                            if (string.IsNullOrEmpty(jsonValue))
                            {
                                missingCount++;
                                _logger.Trace($"CheckProvisioningStatus: {categoryName} — not present in registry");
                                continue;
                            }

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
                        if (_provisioningCategoriesResolved.Count > 0
                            && _lastProvisioningJson.Count > 0
                            && _provisioningCategoriesResolved.Count >= _lastProvisioningJson.Count
                            && !_provisioningFailureFired.Any())
                        {
                            _logger.Info("Provisioning status watcher: all categories resolved with success — requesting stop");
                            _provisioningWatcher?.RequestStop();
                        }

                        // Fire EspFailureDetected AFTER event emission (event in spool before agent reacts)
                        if (failureDetected && failureType != null)
                        {
                            try { EspFailureDetected?.Invoke(this, failureType); }
                            catch (Exception ex) { _logger.Error($"EspFailureDetected handler failed for '{failureType}'", ex); }
                        }

                        // Fire DeviceSetupProvisioningComplete when DeviceSetup resolves with success.
                        if (!_deviceSetupProvisioningCompleteFired
                            && _lastCategorySucceeded.TryGetValue("DeviceSetupCategory.Status", out var dsSucceeded)
                            && dsSucceeded == true)
                        {
                            _deviceSetupProvisioningCompleteFired = true;
                            _logger.Info("Provisioning status: DeviceSetup succeeded — firing DeviceSetupProvisioningComplete");
                            try { DeviceSetupProvisioningComplete?.Invoke(this, EventArgs.Empty); }
                            catch (Exception ex) { _logger.Error("DeviceSetupProvisioningComplete handler failed", ex); }
                        }

                        // Fallback: all subcategories succeeded but categorySucceeded was never set by Windows.
                        // Some Windows builds (e.g. 25H2/26200) don't set the boolean even after all subcategories succeed.
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

                    if (categorySucceeded.HasValue)
                    {
                        _logger.Info($"DeviceSetup fallback timer: categorySucceeded is now {categorySucceeded.Value} — normal path will handle this");
                        CheckProvisioningStatus();
                        return;
                    }

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
                    try { DeviceSetupProvisioningComplete?.Invoke(this, EventArgs.Empty); }
                    catch (Exception ex) { _logger.Error("DeviceSetupProvisioningComplete handler failed (fallback path)", ex); }
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
        internal ProvisioningResult ProcessCategoryStatus(string categoryName, string jsonValue)
        {
            var categoryLabel = categoryName.Replace("Category.Status", "");

            try
            {
                using (var doc = JsonDocument.Parse(jsonValue))
                {
                    var root = doc.RootElement;

                    bool? categorySucceeded = SafeGetBool(root, "categorySucceeded");
                    string categoryStatusMessage = SafeGetString(root, "categoryStatusMessage");
                    var subcategories = ParseSubcategories(root);

                    // Detect WhiteGlove signal in DeviceSetup category.
                    // SaveWhiteGloveSuccessResult is NOT a *Subcategory-suffixed property, so
                    // ParseSubcategories skips it. We scan the raw JSON explicitly.
                    // Track state transitions for full observability (notStarted → succeeded).
                    if (string.Equals(categoryLabel, "DeviceSetup", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var prop in root.EnumerateObject())
                        {
                            if (prop.Name.IndexOf("SaveWhiteGloveSuccessResult", StringComparison.OrdinalIgnoreCase) >= 0
                                && prop.Value.ValueKind == JsonValueKind.Object)
                            {
                                var state = SafeGetString(prop.Value, "subcategoryState") ?? "unknown";

                                if (!string.Equals(state, _lastSaveWhiteGloveState, StringComparison.OrdinalIgnoreCase))
                                {
                                    var previousState = _lastSaveWhiteGloveState ?? "not_seen";
                                    _lastSaveWhiteGloveState = state;

                                    _logger.Info($"ProvisioningStatusTracker: SaveWhiteGloveSuccessResult state " +
                                                 $"transition: {previousState} -> {state}");

                                    EmitRawRegistryDump(categoryLabel, jsonValue,
                                        $"whiteglove_signal_{state}");

                                    if (string.Equals(state, "succeeded", StringComparison.OrdinalIgnoreCase))
                                    {
                                        _saveWhiteGloveSuccessResultSeen = true;
                                        _logger.Info("ProvisioningStatusTracker: SaveWhiteGloveSuccessResult=succeeded " +
                                                     "— WhiteGlove confirmation signal");
                                    }
                                }
                                break;
                            }
                        }
                    }

                    string statusText;
                    if (categoryStatusMessage != null)
                        statusText = categoryStatusMessage;
                    else if (categorySucceeded == true)
                        statusText = "Complete";
                    else if (categorySucceeded == false)
                        statusText = "Failed";
                    else
                        statusText = BuildProgressSummary(subcategories);

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
                            _logger.Trace($"ProcessCategory: {categoryLabel} — EMITTING progress update (JSON changed, no state transitions)");
                            EmitProvisioningEvent(categoryLabel, categorySucceeded, statusText, subcategories, EventSeverity.Info);
                        }
                    }

                    if (categorySucceeded.HasValue)
                        _provisioningCategoriesResolved.Add(categoryName);

                    if (categorySucceeded == false)
                    {
                        return TryFireProvisioningFailure(categoryName, categoryLabel, subcategories);
                    }

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

        private bool HasCategorySucceededChanged(string categoryName, bool? newValue)
        {
            if (!_lastCategorySucceeded.TryGetValue(categoryName, out var oldValue))
                return false;
            return oldValue != newValue;
        }

        private void StoreSubcategoryStates(string categoryName, List<SubcategoryInfo> subcategories)
        {
            var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sub in subcategories)
                states[sub.Name] = sub.State;
            _lastSubcategoryStates[categoryName] = states;
        }

        private List<SubcategoryTransition> DetectSubcategoryTransitions(string categoryName, List<SubcategoryInfo> subcategories)
        {
            var transitions = new List<SubcategoryTransition>();

            if (!_lastSubcategoryStates.TryGetValue(categoryName, out var lastStates))
                return transitions;

            foreach (var sub in subcategories)
            {
                if (!lastStates.TryGetValue(sub.Name, out var oldState))
                    continue;

                if (string.Equals(oldState, sub.State, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool isFailure = string.Equals(sub.State, "failed", StringComparison.OrdinalIgnoreCase);
                bool isCompletion = string.Equals(sub.State, "succeeded", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(oldState, "succeeded", StringComparison.OrdinalIgnoreCase);

                bool isNoise = string.Equals(oldState, "notStarted", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(sub.State, "in_progress", StringComparison.OrdinalIgnoreCase);

                if (isFailure || isCompletion)
                {
                    transitions.Add(new SubcategoryTransition(sub.Name, oldState, sub.State, isFailure));
                }
                else if (!isNoise)
                {
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

                // Flat top-level field for RuleEngine matching — the RuleEngine cannot
                // traverse nested collections via dataField, so we expose the names of
                // subcategories that just transitioned to "failed" as a comma-separated
                // string. Names come from registry keys and are language-invariant.
                var failedNames = transitions
                    .Where(t => t.IsFailure)
                    .Select(t => t.SubcategoryName)
                    .ToList();
                if (failedNames.Count > 0)
                {
                    eventData["failedSubcategories"] = string.Join(",", failedNames);
                }
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

        // =====================================================================
        // JSON parsing helpers (static — exposed as internal for tests)
        // =====================================================================

        internal static List<SubcategoryInfo> ParseSubcategories(JsonElement root)
        {
            var result = new List<SubcategoryInfo>();

            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name.IndexOf("Subcategory", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var name = CleanSubcategoryName(prop.Name);

                switch (prop.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        var text = prop.Value.GetString() ?? "";
                        result.Add(new SubcategoryInfo
                        {
                            Name = name,
                            State = InferStateFromText(text),
                            StatusText = text
                        });
                        break;

                    case JsonValueKind.Object:
                        result.Add(new SubcategoryInfo
                        {
                            Name = name,
                            State = SafeGetString(prop.Value, "subcategoryState") ?? "unknown",
                            StatusText = SafeGetString(prop.Value, "subcategoryStatusText") ?? ""
                        });
                        break;

                    default:
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
        internal static string CleanSubcategoryName(string rawName)
        {
            var name = rawName;

            var idx = name.IndexOf("Subcategory", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                name = name.Substring(0, idx);

            var dotIdx = name.LastIndexOf('.');
            if (dotIdx >= 0 && dotIdx < name.Length - 1)
                name = name.Substring(dotIdx + 1);

            return string.IsNullOrEmpty(name) ? rawName : name;
        }

        internal static string InferStateFromText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "unknown";

            if (text.IndexOf("Complete", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("applied", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("installed", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("added", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("No setup needed", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("Identified", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "succeeded";
            }

            if (text.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("Failed", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("Failure", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "failed";
            }

            return "in_progress";
        }

        internal static string FindFailedSubcategory(List<SubcategoryInfo> subcategories)
        {
            foreach (var sub in subcategories)
            {
                if (string.Equals(sub.State, "failed", StringComparison.OrdinalIgnoreCase))
                    return sub.Name;
            }
            return null;
        }

        internal static string BuildProgressSummary(List<SubcategoryInfo> subcategories)
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

        internal static bool? SafeGetBool(JsonElement element, string propertyName)
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

        internal static string SafeGetString(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var prop))
                return null;

            return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
        }

        // =====================================================================
        // Internal types
        // =====================================================================

        internal class SubcategoryInfo
        {
            public string Name { get; set; }
            public string State { get; set; }      // "succeeded", "failed", "in_progress", "unknown", "notRequired"
            public string StatusText { get; set; }
        }

        internal readonly struct SubcategoryTransition
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

        internal readonly struct ProvisioningResult
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

        // =====================================================================
        // Test seams
        // =====================================================================

        /// <summary>Test-only: drive ProcessCategoryStatus without registry access.</summary>
        internal ProvisioningResult ProcessCategoryStatusForTest(string categoryName, string jsonValue)
        {
            lock (_stateLock)
            {
                EnsureStateDictionariesInitialized();
                return ProcessCategoryStatus(categoryName, jsonValue);
            }
        }

        private void EnsureStateDictionariesInitialized()
        {
            if (_lastProvisioningJson == null)
                _lastProvisioningJson = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (_provisioningCategorySeen == null)
                _provisioningCategorySeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_lastCategorySucceeded == null)
                _lastCategorySucceeded = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
            if (_provisioningFailureFired == null)
                _provisioningFailureFired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_provisioningCategoriesResolved == null)
                _provisioningCategoriesResolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_lastSubcategoryStates == null)
                _lastSubcategoryStates = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Immutable snapshot of ESP provisioning category status at a point in time.
    /// Used to attach to enrollment_complete events and for settle-wait decisions.
    /// </summary>
    public class EspProvisioningSnapshot
    {
        /// <summary>Per-category outcome: "success", "failed", or "in_progress".</summary>
        public Dictionary<string, string> CategoryOutcomes { get; set; }

        /// <summary>Per-category subcategory detail: subcategoryName -> state string.</summary>
        public Dictionary<string, Dictionary<string, string>> SubcategoryStates { get; set; }

        /// <summary>Number of categories seen in the registry.</summary>
        public int CategoriesSeen { get; set; }

        /// <summary>Number of categories that have a final categorySucceeded value.</summary>
        public int CategoriesResolved { get; set; }

        /// <summary>True if all seen categories are resolved (or none seen).</summary>
        public bool AllResolved { get; set; }
    }
}
