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
    /// Supports registry, eventlog, wmi, file, command_allowlisted, and logparser collector types
    /// </summary>
    public class GatherRuleExecutor : IDisposable
    {
        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly Action<EnrollmentEvent> _onEventCollected;
        private readonly string _imeLogPathOverride;

        private List<GatherRule> _activeRules = new List<GatherRule>();
        private readonly Dictionary<string, Timer> _intervalTimers = new Dictionary<string, Timer>();
        private readonly HashSet<string> _startupRulesExecuted = new HashSet<string>();
        private readonly LogFilePositionTracker _filePositionTracker = new LogFilePositionTracker();
        private CountdownEvent _startupRulesLatch;   // non-null only while startup rules are pending

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

            // Guard: only allow enrollment-relevant registry paths
            if (!GatherRuleGuards.IsRegistryPathAllowed(subPath))
                return EmitSecurityWarning(rule, "registry", path);

            // Determine explicit valueName from parameters
            string explicitValueName = null;
            rule.Parameters?.TryGetValue("valueName", out explicitValueName);

            try
            {
                using (var key = hive.OpenSubKey(subPath, false))
                {
                    if (key == null)
                    {
                        // The key doesn't exist as a sub-key. If no explicit valueName was given,
                        // check whether the last path segment is a value name in the parent key.
                        // This handles the common case where the user specifies the full
                        // "HKLM\...\KeyName\ValueName" path directly in Target.
                        if (string.IsNullOrEmpty(explicitValueName))
                        {
                            var lastBackslash = subPath.LastIndexOf('\\');
                            if (lastBackslash > 0)
                            {
                                var parentSubPath = subPath.Substring(0, lastBackslash);
                                var inferredValueName = subPath.Substring(lastBackslash + 1);

                                using (var parentKey = hive.OpenSubKey(parentSubPath, false))
                                {
                                    if (parentKey != null)
                                    {
                                        var valueNames = parentKey.GetValueNames();
                                        if (Array.Exists(valueNames, n => string.Equals(n, inferredValueName, StringComparison.OrdinalIgnoreCase)))
                                        {
                                            var value = parentKey.GetValue(inferredValueName);
                                            data["exists"] = true;
                                            data["path"] = path.Substring(0, path.LastIndexOf('\\'));
                                            data[inferredValueName] = value?.ToString();
                                            return data;
                                        }
                                    }
                                }
                            }
                        }

                        data["exists"] = false;
                        data["path"] = path;
                        return data;
                    }

                    data["exists"] = true;
                    data["path"] = path;

                    // Read specific value if specified in parameters
                    if (!string.IsNullOrEmpty(explicitValueName))
                    {
                        var value = key.GetValue(explicitValueName);
                        data[explicitValueName] = value?.ToString();
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

            // Guard: only allow known-safe WMI classes
            if (!GatherRuleGuards.IsWmiQueryAllowed(query))
                return EmitSecurityWarning(rule, "wmi", query);

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
            if (!GatherRuleGuards.IsCommandAllowed(command))
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
                    // Use -EncodedCommand with Base64-encoded UTF-16LE to prevent command injection
                    var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
                    psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy RemoteSigned -EncodedCommand {encodedCommand}",
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
                        data["output"] = output.Length > 32000 ? output.Substring(0, 32000) + "... (truncated)" : output;
                    }

                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        data["error_output"] = error.Length > 8000 ? error.Substring(0, 8000) + "... (truncated)" : error;
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

            // Guard: only allow enrollment-relevant file paths
            if (!GatherRuleGuards.IsFilePathAllowed(filePath))
                return EmitSecurityWarning(rule, "file", filePath);

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
                        // Read the last 4000 chars — most useful for log files where recent entries matter
                        const int maxChars = 4000;
                        string content;
                        bool truncated = false;
                        using (var reader = new StreamReader(filePath))
                        {
                            var full = reader.ReadToEnd();
                            if (full.Length > maxChars)
                            {
                                content = full.Substring(full.Length - maxChars);
                                truncated = true;
                            }
                            else
                            {
                                content = full;
                            }
                        }
                        data["content"] = truncated ? "(truncated) ..." + content : content;
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
                if (rule.Parameters != null && rule.Parameters.TryGetValue("maxEntries", out var maxEntriesStr))
                {
                    int.TryParse(maxEntriesStr, out maxEntries);
                    maxEntries = Math.Min(maxEntries, 50); // Cap at 50
                }

                // Build XPath query with optional filters
                var conditions = new List<string>();

                string sourceFilter = null;
                if (rule.Parameters != null && rule.Parameters.TryGetValue("source", out sourceFilter)
                    && !string.IsNullOrEmpty(sourceFilter))
                {
                    conditions.Add($"Provider[@Name='{EscapeXPath(sourceFilter)}']");
                }

                string eventIdFilter = null;
                if (rule.Parameters != null && rule.Parameters.TryGetValue("eventId", out eventIdFilter)
                    && !string.IsNullOrEmpty(eventIdFilter))
                {
                    conditions.Add($"EventID={eventIdFilter}");
                }

                string xpath = conditions.Count > 0
                    ? $"*[System[{string.Join(" and ", conditions)}]]"
                    : "*";

                string messageFilter = null;
                rule.Parameters?.TryGetValue("messageFilter", out messageFilter);

                var query = new EventLogQuery(logName, PathType.LogName, xpath)
                {
                    ReverseDirection = true // newest first
                };

                data["log_name"] = logName;
                var entries = new List<string>();

                using (var reader = new EventLogReader(query))
                {
                    EventRecord record;
                    while ((record = reader.ReadEvent()) != null && entries.Count < maxEntries)
                    {
                        using (record)
                        {
                            var message = "";
                            try { message = record.FormatDescription() ?? ""; }
                            catch { /* Some events lack formatting resources */ }

                            // Apply message filter (wildcard contains)
                            if (!string.IsNullOrEmpty(messageFilter))
                            {
                                var pattern = messageFilter.Trim('*');
                                if (string.IsNullOrEmpty(pattern) ||
                                    message.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) < 0)
                                    continue;
                            }

                            var level = "";
                            try { level = record.LevelDisplayName ?? record.Level?.ToString() ?? "Unknown"; }
                            catch { level = record.Level?.ToString() ?? "Unknown"; }

                            var truncMsg = message.Length > 500 ? message.Substring(0, 500) : message;
                            entries.Add($"[{record.TimeCreated:yyyy-MM-dd HH:mm:ss}] [{level}] [ID:{record.Id}] {truncMsg}");
                        }
                    }
                }

                data["entries"] = string.Join("\n", entries);
                data["entries_returned"] = entries.Count;
            }
            catch (Exception ex)
            {
                data["error"] = ex.Message;
            }

            return data;
        }

        private static string EscapeXPath(string value)
        {
            return value.Replace("'", "&apos;");
        }

        private void ExecuteLogParserRule(GatherRule rule)
        {
            var filePath = rule.Target;
            if (string.IsNullOrEmpty(filePath))
                return;

            filePath = Environment.ExpandEnvironmentVariables(filePath);

            // Apply IME log path override if set
            if (!string.IsNullOrEmpty(_imeLogPathOverride))
            {
                var fileName = Path.GetFileName(filePath);
                filePath = Path.Combine(_imeLogPathOverride, fileName);
            }

            // Get parameters
            string patternStr;
            if (rule.Parameters == null || !rule.Parameters.TryGetValue("pattern", out patternStr) ||
                string.IsNullOrEmpty(patternStr))
            {
                _logger.Warning($"LogParser rule {rule.RuleId} has no 'pattern' parameter");
                return;
            }

            string trackPositionStr;
            bool trackPosition = true;
            if (rule.Parameters.TryGetValue("trackPosition", out trackPositionStr))
                bool.TryParse(trackPositionStr, out trackPosition);

            string maxLinesStr;
            int maxLines = 1000;
            if (rule.Parameters.TryGetValue("maxLines", out maxLinesStr))
                int.TryParse(maxLinesStr, out maxLines);

            Regex pattern;
            try
            {
                pattern = new Regex(patternStr, RegexOptions.Compiled, TimeSpan.FromSeconds(1));
            }
            catch (Exception ex)
            {
                _logger.Warning($"LogParser rule {rule.RuleId} has invalid regex: {ex.Message}");
                return;
            }

            if (!File.Exists(filePath))
            {
                _logger.Debug($"LogParser rule {rule.RuleId}: file not found: {filePath}");
                return;
            }

            try
            {
                var fileInfo = new FileInfo(filePath);
                long startPosition = trackPosition
                    ? _filePositionTracker.GetSafePosition(filePath, fileInfo.Length)
                    : 0;

                // Nothing new to read
                if (startPosition >= fileInfo.Length)
                    return;

                int matchCount = 0;
                int linesRead = 0;
                long endPosition = startPosition;

                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    stream.Seek(startPosition, SeekOrigin.Begin);

                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null && linesRead < maxLines)
                        {
                            linesRead++;

                            CmTraceLogEntry entry;
                            if (!CmTraceLogParser.TryParseLine(line, out entry))
                                continue;

                            Match match;
                            try
                            {
                                match = pattern.Match(entry.Message);
                            }
                            catch (RegexMatchTimeoutException)
                            {
                                continue;
                            }
                            if (!match.Success)
                                continue;

                            // Build data dictionary from named capture groups
                            var data = new Dictionary<string, object>();
                            foreach (var groupName in pattern.GetGroupNames())
                            {
                                if (groupName == "0") continue; // Skip full match group
                                var group = match.Groups[groupName];
                                if (group.Success)
                                    data[groupName] = group.Value;
                            }

                            // Add CMTrace metadata
                            data["logTimestamp"] = entry.Timestamp.ToString("o");
                            data["logComponent"] = entry.Component;
                            data["logType"] = entry.Type;
                            data["logMessage"] = TruncateMessage(entry.Message, 500);
                            data["ruleId"] = rule.RuleId;
                            data["ruleTitle"] = rule.Title;

                            var eventType = !string.IsNullOrEmpty(rule.OutputEventType)
                                ? rule.OutputEventType
                                : "logparser_match";
                            var severity = MapCmTraceTypeToSeverity(entry.Type, rule.OutputSeverity);

                            _onEventCollected(new EnrollmentEvent
                            {
                                SessionId = _sessionId,
                                TenantId = _tenantId,
                                Timestamp = entry.Timestamp,
                                EventType = eventType,
                                Severity = severity,
                                Source = "GatherRuleExecutor",
                                Message = $"Gather: {rule.Title}",
                                Data = data
                            });

                            matchCount++;
                        }

                        // Capture the final stream position after reading
                        endPosition = stream.Position;
                    }
                }

                if (trackPosition)
                    _filePositionTracker.SetPosition(filePath, endPosition);

                if (matchCount > 0)
                    _logger.Debug($"LogParser rule {rule.RuleId}: {matchCount} matches from {linesRead} lines in {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"LogParser rule {rule.RuleId} failed reading {filePath}: {ex.Message}");
            }
        }

        private static EventSeverity MapCmTraceTypeToSeverity(int cmTraceType, string ruleOverride)
        {
            // If the rule specifies a severity, use it
            if (!string.IsNullOrEmpty(ruleOverride))
            {
                switch (ruleOverride.ToLower())
                {
                    case "debug": return EventSeverity.Debug;
                    case "info": return EventSeverity.Info;
                    case "warning": return EventSeverity.Warning;
                    case "error": return EventSeverity.Error;
                    case "critical": return EventSeverity.Critical;
                }
            }

            // Otherwise derive from CMTrace log type
            switch (cmTraceType)
            {
                case 2: return EventSeverity.Warning;
                case 3: return EventSeverity.Error;
                default: return EventSeverity.Info;
            }
        }

        private static string TruncateMessage(string message, int maxLength)
        {
            if (string.IsNullOrEmpty(message) || message.Length <= maxLength)
                return message;
            return message.Substring(0, maxLength) + "...";
        }

        /// <summary>
        /// Emits a security_warning event and returns an empty result dictionary.
        /// Called when a gather rule targets a path/query not on the allowlist.
        /// </summary>
        private Dictionary<string, object> EmitSecurityWarning(GatherRule rule, string collectorType, string target)
        {
            _logger.Warning($"SECURITY: {collectorType} path blocked by guard: {target} (Rule: {rule.RuleId})");

            var data = new Dictionary<string, object>
            {
                ["blocked"] = true,
                ["reason"] = $"{collectorType} target not on allowlist",
                ["target"] = target,
                ["ruleId"] = rule.RuleId,
            };

            _onEventCollected(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                Timestamp = DateTime.UtcNow,
                EventType = "security_warning",
                Severity = EventSeverity.Warning,
                Source = "GatherRuleExecutor",
                Message = $"Blocked {collectorType} target not on allowlist: {target} (Rule: {rule.RuleId})",
                Data = data
            });

            return new Dictionary<string, object>();
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

        /// <summary>
        /// Blocks until all startup rules that were queued by the most recent <see cref="UpdateRules"/>
        /// call have finished executing, or the timeout elapses.
        /// Returns true if all rules completed within the timeout; false if timed out.
        /// </summary>
        public bool WaitForStartupRules(int timeoutSeconds = 120)
        {
            var latch = _startupRulesLatch;
            if (latch == null)
                return true;  // no startup rules were queued

            _logger.Debug($"WaitForStartupRules: waiting up to {timeoutSeconds}s for startup rules to complete...");
            var completed = latch.Wait(TimeSpan.FromSeconds(timeoutSeconds));
            _logger.Debug($"WaitForStartupRules: {(completed ? "all rules completed" : "timed out")}");
            return completed;
        }

        public void Dispose()
        {
            StopAllTimers();
            _startupRulesLatch?.Dispose();
            _startupRulesLatch = null;
        }
    }
}
