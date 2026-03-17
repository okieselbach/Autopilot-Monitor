using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Agent.Core.Monitoring.Tracking;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.Core.Monitoring.Collectors
{
    /// <summary>
    /// Executes gather rules received from the backend API
    /// Supports registry, eventlog, wmi, file, command_allowlisted, logparser, json, and xml collector types
    /// </summary>
    public partial class GatherRuleExecutor : IDisposable
    {
        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly Action<EnrollmentEvent> _onEventCollected;
        private readonly string _imeLogPathOverride;

        private List<GatherRule> _activeRules = new List<GatherRule>();
        private readonly Dictionary<string, Timer> _intervalTimers = new Dictionary<string, Timer>();
        private readonly HashSet<string> _startupRulesExecuted = new HashSet<string>();
        private readonly HashSet<string> _phaseRulesExecuted = new HashSet<string>();
        private readonly LogFilePositionTracker _filePositionTracker = new LogFilePositionTracker();
        private CountdownEvent _startupRulesLatch;   // non-null only while startup rules are pending

        /// <summary>
        /// When true, guardrails are relaxed: all registry, WMI, and command targets are allowed.
        /// File paths allow everything except C:\Users. Set from tenant configuration.
        /// </summary>
        public bool UnrestrictedMode { get; set; } = false;

        public GatherRuleExecutor(string sessionId, string tenantId, Action<EnrollmentEvent> onEventCollected,
            AgentLogger logger, string imeLogPathOverride = null)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _onEventCollected = onEventCollected ?? throw new ArgumentNullException(nameof(onEventCollected));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _imeLogPathOverride = imeLogPathOverride;
        }

        /// <summary>
        /// Updates the active rules and starts/stops execution accordingly
        /// </summary>
        public void UpdateRules(List<GatherRule> rules)
        {
            if (rules == null)
                return;

            _logger.Info($"GatherRuleExecutor: updating with {rules.Count} active rules");

            // Stop existing interval timers
            StopAllTimers();

            _activeRules = rules.Where(r => r.Enabled).ToList();

            // Execute startup rules — track completion via CountdownEvent so callers can wait
            var pendingStartup = _activeRules
                .Where(r => r.Trigger == "startup" && !_startupRulesExecuted.Contains(r.RuleId))
                .ToList();

            if (pendingStartup.Count > 0)
            {
                _startupRulesLatch?.Dispose();
                _startupRulesLatch = new CountdownEvent(pendingStartup.Count);

                foreach (var rule in pendingStartup)
                {
                    _startupRulesExecuted.Add(rule.RuleId);
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try   { ExecuteRule(rule); }
                        finally { _startupRulesLatch?.Signal(); }
                    });
                }
            }

            // Set up interval timers
            foreach (var rule in _activeRules.Where(r => r.Trigger == "interval" && r.IntervalSeconds.HasValue))
            {
                var interval = TimeSpan.FromSeconds(rule.IntervalSeconds.Value);
                var timer = new Timer(
                    _ => ExecuteRule(rule),
                    null,
                    interval, // Initial delay = one interval
                    interval
                );
                _intervalTimers[rule.RuleId] = timer;
                _logger.Info($"  Interval rule {rule.RuleId} scheduled every {rule.IntervalSeconds}s");
            }

            _logger.Info($"GatherRuleExecutor: {_activeRules.Count(r => r.Trigger == "startup")} startup, " +
                         $"{_intervalTimers.Count} interval rules active");
        }

        /// <summary>
        /// Called when a phase change event occurs - executes rules triggered by phase changes
        /// </summary>
        public void OnPhaseChanged(EnrollmentPhase newPhase)
        {
            var phaseName = newPhase.ToString();

            foreach (var rule in _activeRules.Where(r => r.Trigger == "phase_change"))
            {
                if (string.IsNullOrEmpty(rule.TriggerPhase) ||
                    string.Equals(rule.TriggerPhase, phaseName, StringComparison.OrdinalIgnoreCase))
                {
                    // Deduplicate: only fire once per (ruleId, phase) combination
                    var deduplicationKey = $"{rule.RuleId}|{phaseName}";
                    if (!_phaseRulesExecuted.Add(deduplicationKey))
                    {
                        _logger.Debug($"Phase rule {rule.RuleId} already executed for phase {phaseName}, skipping");
                        continue;
                    }

                    _logger.Info($"Phase change triggered rule {rule.RuleId} (phase: {phaseName})");
                    ThreadPool.QueueUserWorkItem(_ => ExecuteRule(rule));
                }
            }
        }

        /// <summary>
        /// Called when a specific event type is emitted - executes on_event rules
        /// </summary>
        public void OnEvent(string eventType)
        {
            foreach (var rule in _activeRules.Where(r => r.Trigger == "on_event"))
            {
                if (string.Equals(rule.TriggerEventType, eventType, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Info($"Event triggered rule {rule.RuleId} (event: {eventType})");
                    ThreadPool.QueueUserWorkItem(_ => ExecuteRule(rule));
                }
            }
        }

        private void ExecuteRule(GatherRule rule)
        {
            try
            {
                _logger.Info($"Executing gather rule: {rule.RuleId} ({rule.Title})");

                Dictionary<string, object> result = null;

                switch (rule.CollectorType?.ToLower())
                {
                    case "registry":
                        result = ExecuteRegistryRule(rule);
                        break;

                    case "wmi":
                        result = ExecuteWmiRule(rule);
                        break;

                    case "command_allowlisted":
                    case "command": // legacy alias - both enforce the allowlist
                        result = ExecuteCommandRule(rule);
                        break;

                    case "file":
                        result = ExecuteFileRule(rule);
                        break;

                    case "eventlog":
                        result = ExecuteEventLogRule(rule);
                        break;

                    case "json":
                        result = ExecuteJsonRule(rule);
                        break;

                    case "xml":
                        result = ExecuteXmlRule(rule);
                        break;

                    case "logparser":
                        ExecuteLogParserRule(rule);
                        return; // Return early - logparser emits events directly

                    default:
                        _logger.Warning($"Unknown collector type: {rule.CollectorType} for rule {rule.RuleId}");
                        return;
                }

                if (result != null && result.Count > 0)
                {
                    result["ruleId"] = rule.RuleId;
                    result["ruleTitle"] = rule.Title;

                    var eventType = !string.IsNullOrEmpty(rule.OutputEventType) ? rule.OutputEventType : "gather_result";

                    // Allow collectors to override severity via _severityOverride in result
                    object severityOverride;
                    EventSeverity severity;
                    if (result.TryGetValue("_severityOverride", out severityOverride) && severityOverride is string sev)
                    {
                        severity = ParseSeverity(sev);
                        result.Remove("_severityOverride");
                    }
                    else
                    {
                        severity = ParseSeverity(rule.OutputSeverity);
                    }

                    _onEventCollected(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        Timestamp = DateTime.UtcNow,
                        EventType = eventType,
                        Severity = severity,
                        Source = "GatherRuleExecutor",
                        Message = $"Gather: {rule.Title}",
                        Data = result
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Gather rule {rule.RuleId} failed: {ex.Message}");
            }
        }

    }
}
