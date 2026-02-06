using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.Core.EventCollection
{
    /// <summary>
    /// Executes gather rules received from the backend API
    /// Supports registry, eventlog, wmi, file, and command (allowlisted) collector types
    /// </summary>
    public class GatherRuleExecutor : IDisposable
    {
        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly Action<EnrollmentEvent> _onEventCollected;

        private List<GatherRule> _activeRules = new List<GatherRule>();
        private readonly Dictionary<string, Timer> _intervalTimers = new Dictionary<string, Timer>();
        private readonly HashSet<string> _startupRulesExecuted = new HashSet<string>();

        /// <summary>
        /// Strict command allowlist - only these commands can be executed via gather rules
        /// This is a security boundary: commands not on this list are REJECTED
        /// </summary>
        private static readonly HashSet<string> CommandAllowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // TPM and Security
            "Get-Tpm",
            "Get-SecureBootPolicy",
            "Get-SecureBootUEFI -Name SetupMode",

            // BitLocker
            "Get-BitLockerVolume -MountPoint C:",

            // Network
            "Get-NetAdapter | Select-Object Name, Status, InterfaceDescription, MacAddress, LinkSpeed",
            "Get-DnsClientServerAddress | Select-Object InterfaceAlias, ServerAddresses",
            "Get-NetIPConfiguration | Select-Object InterfaceAlias, IPv4Address, IPv4DefaultGateway, DNSServer",
            "netsh winhttp show proxy",
            "ipconfig /all",

            // Domain / Identity
            "nltest /dsgetdc:",
            "dsregcmd /status",

            // Certificate
            "certutil -store My",

            // Windows Update
            "Get-HotFix | Select-Object -First 10 HotFixID, InstalledOn, Description"
        };

        public GatherRuleExecutor(string sessionId, string tenantId, Action<EnrollmentEvent> onEventCollected,
            AgentLogger logger)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _onEventCollected = onEventCollected ?? throw new ArgumentNullException(nameof(onEventCollected));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

            // Execute startup rules
            foreach (var rule in _activeRules.Where(r => r.Trigger == "startup"))
            {
                if (!_startupRulesExecuted.Contains(rule.RuleId))
                {
                    _startupRulesExecuted.Add(rule.RuleId);
                    ThreadPool.QueueUserWorkItem(_ => ExecuteRule(rule));
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
                _logger.Debug($"  Interval rule {rule.RuleId} scheduled every {rule.IntervalSeconds}s");
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
                    _logger.Debug($"Phase change triggered rule {rule.RuleId} (phase: {phaseName})");
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
                    _logger.Debug($"Event triggered rule {rule.RuleId} (event: {eventType})");
                    ThreadPool.QueueUserWorkItem(_ => ExecuteRule(rule));
                }
            }
        }

        private void ExecuteRule(GatherRule rule)
        {
            try
            {
                _logger.Debug($"Executing gather rule: {rule.RuleId} ({rule.Title})");

                Dictionary<string, object> result = null;

                switch (rule.CollectorType?.ToLower())
                {
                    case "registry":
                        result = ExecuteRegistryRule(rule);
                        break;

                    case "wmi":
                        result = ExecuteWmiRule(rule);
                        break;

                    case "command":
                        result = ExecuteCommandRule(rule);
                        break;

                    case "file":
                        result = ExecuteFileRule(rule);
                        break;

                    case "eventlog":
                        result = ExecuteEventLogRule(rule);
                        break;

                    default:
                        _logger.Warning($"Unknown collector type: {rule.CollectorType} for rule {rule.RuleId}");
                        return;
                }

                if (result != null && result.Count > 0)
                {
                    result["ruleId"] = rule.RuleId;
                    result["ruleTitle"] = rule.Title;

                    var eventType = !string.IsNullOrEmpty(rule.OutputEventType) ? rule.OutputEventType : "gather_result";
                    var severity = ParseSeverity(rule.OutputSeverity);

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

        private Dictionary<string, object> ExecuteRegistryRule(GatherRule rule)
        {
            var data = new Dictionary<string, object>();
            var path = rule.Target;

            if (string.IsNullOrEmpty(path))
                return data;

            // Determine hive
            RegistryKey hive = null;
            string subPath = path;

            if (path.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase))
            {
                hive = Registry.LocalMachine;
                subPath = path.Substring(path.IndexOf('\\') + 1);
            }
            else if (path.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase) ||
                     path.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase))
            {
                hive = Registry.CurrentUser;
                subPath = path.Substring(path.IndexOf('\\') + 1);
            }
            else
            {
                // Default to HKLM
                hive = Registry.LocalMachine;
            }

            try
            {
                using (var key = hive.OpenSubKey(subPath, false))
                {
                    if (key == null)
                    {
                        data["exists"] = false;
                        data["path"] = path;
                        return data;
                    }

                    data["exists"] = true;
                    data["path"] = path;

                    // Read specific value if specified in parameters
                    string valueName;
                    if (rule.Parameters != null && rule.Parameters.TryGetValue("valueName", out valueName))
                    {
                        var value = key.GetValue(valueName);
                        data[valueName] = value?.ToString();
                    }
                    else
                    {
                        // Read all values
                        foreach (var name in key.GetValueNames().Take(50)) // Limit to 50 values
                        {
                            try
                            {
                                var value = key.GetValue(name);
                                var displayName = string.IsNullOrEmpty(name) ? "(Default)" : name;
                                data[displayName] = value?.ToString();
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                data["error"] = ex.Message;
            }

            return data;
        }

        private Dictionary<string, object> ExecuteWmiRule(GatherRule rule)
        {
            var data = new Dictionary<string, object>();
            var query = rule.Target;

            if (string.IsNullOrEmpty(query))
                return data;

            try
            {
                using (var searcher = new ManagementObjectSearcher(query))
                {
                    int index = 0;
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var item = new Dictionary<string, object>();
                        foreach (var prop in obj.Properties)
                        {
                            try
                            {
                                item[prop.Name] = prop.Value?.ToString();
                            }
                            catch { }
                        }

                        if (item.Count > 0)
                        {
                            // For single result, flatten; for multiple, use indexed keys
                            if (index == 0)
                            {
                                foreach (var kvp in item)
                                    data[kvp.Key] = kvp.Value;
                            }
                            else
                            {
                                data[$"item_{index}"] = string.Join(", ", item.Select(k => $"{k.Key}={k.Value}"));
                            }
                        }
                        index++;

                        if (index >= 20) break; // Limit results
                    }
                }
            }
            catch (Exception ex)
            {
                data["error"] = ex.Message;
            }

            return data;
        }

        private Dictionary<string, object> ExecuteCommandRule(GatherRule rule)
        {
            var data = new Dictionary<string, object>();
            var command = rule.Target;

            if (string.IsNullOrEmpty(command))
                return data;

            // SECURITY: Check command against allowlist
            if (!IsCommandAllowed(command))
            {
                _logger.Warning($"SECURITY: Command blocked by allowlist: {command} (Rule: {rule.RuleId})");
                data["blocked"] = true;
                data["reason"] = "Command not on allowlist";
                data["command"] = command;

                _onEventCollected(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    Timestamp = DateTime.UtcNow,
                    EventType = "security_warning",
                    Severity = EventSeverity.Warning,
                    Source = "GatherRuleExecutor",
                    Message = $"Blocked command not on allowlist: {command} (Rule: {rule.RuleId})",
                    Data = data
                });

                return data;
            }

            try
            {
                var isPowerShell = !command.StartsWith("netsh", StringComparison.OrdinalIgnoreCase) &&
                                   !command.StartsWith("ipconfig", StringComparison.OrdinalIgnoreCase) &&
                                   !command.StartsWith("nltest", StringComparison.OrdinalIgnoreCase) &&
                                   !command.StartsWith("certutil", StringComparison.OrdinalIgnoreCase) &&
                                   !command.StartsWith("dsregcmd", StringComparison.OrdinalIgnoreCase);

                ProcessStartInfo psi;
                if (isPowerShell)
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                }
                else
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c {command}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                }

                using (var process = Process.Start(psi))
                {
                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();
                    process.WaitForExit(30000); // 30 second timeout

                    data["command"] = command;
                    data["exit_code"] = process.ExitCode;

                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        // Truncate large output
                        data["output"] = output.Length > 4000 ? output.Substring(0, 4000) + "... (truncated)" : output;
                    }

                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        data["error_output"] = error.Length > 1000 ? error.Substring(0, 1000) + "... (truncated)" : error;
                    }
                }
            }
            catch (Exception ex)
            {
                data["error"] = ex.Message;
            }

            return data;
        }

        private Dictionary<string, object> ExecuteFileRule(GatherRule rule)
        {
            var data = new Dictionary<string, object>();
            var filePath = rule.Target;

            if (string.IsNullOrEmpty(filePath))
                return data;

            // Expand environment variables
            filePath = Environment.ExpandEnvironmentVariables(filePath);

            try
            {
                if (File.Exists(filePath))
                {
                    var info = new FileInfo(filePath);
                    data["exists"] = true;
                    data["path"] = filePath;
                    data["size_bytes"] = info.Length;
                    data["last_modified"] = info.LastWriteTimeUtc.ToString("o");
                    data["created"] = info.CreationTimeUtc.ToString("o");

                    // Read content if requested and file is small enough
                    string readContent;
                    if (rule.Parameters != null && rule.Parameters.TryGetValue("readContent", out readContent) &&
                        readContent == "true" && info.Length < 50000) // Max 50KB
                    {
                        var content = File.ReadAllText(filePath);
                        data["content"] = content.Length > 4000 ? content.Substring(0, 4000) + "... (truncated)" : content;
                    }
                }
                else if (Directory.Exists(filePath))
                {
                    data["exists"] = true;
                    data["is_directory"] = true;
                    data["path"] = filePath;

                    var files = Directory.GetFiles(filePath);
                    data["file_count"] = files.Length;
                    data["files"] = string.Join(", ", files.Select(Path.GetFileName).Take(20));
                }
                else
                {
                    data["exists"] = false;
                    data["path"] = filePath;
                }
            }
            catch (Exception ex)
            {
                data["error"] = ex.Message;
            }

            return data;
        }

        private Dictionary<string, object> ExecuteEventLogRule(GatherRule rule)
        {
            var data = new Dictionary<string, object>();
            var logName = rule.Target;

            if (string.IsNullOrEmpty(logName))
                return data;

            try
            {
                int maxEntries = 10;
                string maxEntriesStr;
                if (rule.Parameters != null && rule.Parameters.TryGetValue("maxEntries", out maxEntriesStr))
                {
                    int.TryParse(maxEntriesStr, out maxEntries);
                    maxEntries = Math.Min(maxEntries, 50); // Cap at 50
                }

                using (var eventLog = new EventLog(logName))
                {
                    data["log_name"] = logName;
                    data["total_entries"] = eventLog.Entries.Count;

                    var entries = new List<string>();
                    var startIndex = Math.Max(0, eventLog.Entries.Count - maxEntries);

                    for (int i = eventLog.Entries.Count - 1; i >= startIndex && entries.Count < maxEntries; i--)
                    {
                        try
                        {
                            var entry = eventLog.Entries[i];

                            // Apply source filter if specified
                            string sourceFilter;
                            if (rule.Parameters != null && rule.Parameters.TryGetValue("source", out sourceFilter))
                            {
                                if (!string.Equals(entry.Source, sourceFilter, StringComparison.OrdinalIgnoreCase))
                                    continue;
                            }

                            entries.Add($"[{entry.TimeGenerated:yyyy-MM-dd HH:mm:ss}] [{entry.EntryType}] [{entry.Source}] {entry.Message?.Substring(0, Math.Min(200, entry.Message?.Length ?? 0))}");
                        }
                        catch { }
                    }

                    data["entries"] = string.Join("\n", entries);
                    data["entries_returned"] = entries.Count;
                }
            }
            catch (Exception ex)
            {
                data["error"] = ex.Message;
            }

            return data;
        }

        /// <summary>
        /// Checks if a command is on the allowlist
        /// Uses exact match comparison for security
        /// </summary>
        private bool IsCommandAllowed(string command)
        {
            if (string.IsNullOrEmpty(command))
                return false;

            var trimmed = command.Trim();
            return CommandAllowlist.Contains(trimmed);
        }

        private EventSeverity ParseSeverity(string severity)
        {
            if (string.IsNullOrEmpty(severity))
                return EventSeverity.Info;

            switch (severity.ToLower())
            {
                case "debug": return EventSeverity.Debug;
                case "info": return EventSeverity.Info;
                case "warning": return EventSeverity.Warning;
                case "error": return EventSeverity.Error;
                case "critical": return EventSeverity.Critical;
                default: return EventSeverity.Info;
            }
        }

        private void StopAllTimers()
        {
            foreach (var timer in _intervalTimers.Values)
            {
                timer.Dispose();
            }
            _intervalTimers.Clear();
        }

        public void Dispose()
        {
            StopAllTimers();
        }
    }
}
