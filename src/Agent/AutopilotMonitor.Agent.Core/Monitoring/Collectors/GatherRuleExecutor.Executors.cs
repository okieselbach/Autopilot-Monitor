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
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Agent.Core.Monitoring.Tracking;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Agent.Core.Monitoring.Collectors
{
    /// <summary>
    /// Partial: Individual rule executor implementations (registry, WMI, command, file, eventlog, logparser, json, xml).
    /// </summary>
    public partial class GatherRuleExecutor
    {
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
            if (!GatherRuleGuards.IsRegistryPathAllowed(subPath, UnrestrictedMode))
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
            if (!GatherRuleGuards.IsWmiQueryAllowed(query, UnrestrictedMode))
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
            if (!GatherRuleGuards.IsCommandAllowed(command, UnrestrictedMode))
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
            if (!GatherRuleGuards.IsFilePathAllowed(filePath, UnrestrictedMode))
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

            // Determine format: "cmtrace" (default) or "text" for plain text logs
            string formatStr;
            bool isTextMode = false;
            if (rule.Parameters.TryGetValue("format", out formatStr))
                isTextMode = string.Equals(formatStr, "text", StringComparison.OrdinalIgnoreCase);

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

            // Resolve file paths — supports wildcards (* and ?) in the filename portion
            var resolvedPaths = ResolveLogPaths(filePath, rule.RuleId);
            if (resolvedPaths.Count == 0)
            {
                _logger.Debug($"LogParser rule {rule.RuleId}: no files found for: {filePath}");
                return;
            }

            foreach (var resolvedPath in resolvedPaths)
            {
                ProcessLogFile(resolvedPath, rule, pattern, trackPosition, maxLines, isTextMode);
            }
        }

        private List<string> ResolveLogPaths(string filePath, string ruleId)
        {
            var fileNamePart = Path.GetFileName(filePath);

            // No wildcards — single file
            if (!fileNamePart.Contains("*") && !fileNamePart.Contains("?"))
            {
                if (File.Exists(filePath))
                    return new List<string> { filePath };
                return new List<string>();
            }

            // Wildcard expansion
            var directory = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return new List<string>();

            try
            {
                // Return matched files sorted by last write time (newest first), capped at 20
                return Directory.GetFiles(directory, fileNamePart)
                    .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                    .Take(20)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.Warning($"LogParser rule {ruleId}: wildcard expansion failed for {filePath}: {ex.Message}");
                return new List<string>();
            }
        }

        private void ProcessLogFile(string filePath, GatherRule rule, Regex pattern,
            bool trackPosition, int maxLines, bool isTextMode)
        {
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

                            if (isTextMode)
                            {
                                // Text mode: match regex directly against the raw line
                                Match match;
                                try
                                {
                                    match = pattern.Match(line);
                                }
                                catch (RegexMatchTimeoutException)
                                {
                                    continue;
                                }
                                if (!match.Success)
                                    continue;

                                var data = new Dictionary<string, object>();
                                foreach (var groupName in pattern.GetGroupNames())
                                {
                                    if (groupName == "0") continue;
                                    var group = match.Groups[groupName];
                                    if (group.Success)
                                        data[groupName] = group.Value;
                                }

                                data["logLine"] = TruncateMessage(line, 500);
                                data["logLineNumber"] = linesRead;
                                data["logFile"] = Path.GetFileName(filePath);
                                data["ruleId"] = rule.RuleId;
                                data["ruleTitle"] = rule.Title;

                                var eventType = !string.IsNullOrEmpty(rule.OutputEventType)
                                    ? rule.OutputEventType
                                    : "logparser_match";
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
                                    Data = data
                                });

                                matchCount++;
                            }
                            else
                            {
                                // CMTrace mode: parse line as CMTrace format, match regex against message
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

                                var data = new Dictionary<string, object>();
                                foreach (var groupName in pattern.GetGroupNames())
                                {
                                    if (groupName == "0") continue;
                                    var group = match.Groups[groupName];
                                    if (group.Success)
                                        data[groupName] = group.Value;
                                }

                                data["logTimestamp"] = entry.Timestamp.ToString("o");
                                data["logComponent"] = entry.Component;
                                data["logType"] = entry.Type;
                                data["logMessage"] = TruncateMessage(entry.Message, 500);
                                data["logFile"] = Path.GetFileName(filePath);
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

        private Dictionary<string, object> ExecuteJsonRule(GatherRule rule)
        {
            var data = new Dictionary<string, object>();
            var filePath = rule.Target;

            if (string.IsNullOrEmpty(filePath))
                return data;

            filePath = Environment.ExpandEnvironmentVariables(filePath);

            if (!GatherRuleGuards.IsFilePathAllowed(filePath, UnrestrictedMode))
                return EmitSecurityWarning(rule, "json", filePath);

            string jsonPath;
            if (rule.Parameters == null || !rule.Parameters.TryGetValue("jsonpath", out jsonPath) ||
                string.IsNullOrEmpty(jsonPath))
            {
                _logger.Warning($"JSON rule {rule.RuleId} has no 'jsonpath' parameter");
                data["error"] = "Missing required 'jsonpath' parameter";
                return data;
            }

            int maxResults = 20;
            string maxResultsStr;
            if (rule.Parameters.TryGetValue("maxResults", out maxResultsStr))
                int.TryParse(maxResultsStr, out maxResults);
            maxResults = Math.Min(maxResults, 100);

            try
            {
                if (!File.Exists(filePath))
                {
                    data["exists"] = false;
                    data["path"] = filePath;
                    return data;
                }

                var info = new FileInfo(filePath);
                if (info.Length > 200 * 1024) // 200 KB limit
                {
                    data["error"] = $"File too large ({info.Length} bytes, max 200 KB)";
                    data["path"] = filePath;
                    return data;
                }

                string content;
                using (var reader = new StreamReader(filePath))
                {
                    content = reader.ReadToEnd();
                }

                var token = JToken.Parse(content);
                var results = token.SelectTokens(jsonPath).Take(maxResults).ToList();

                data["path"] = filePath;
                data["query"] = jsonPath;
                data["matchCount"] = results.Count;

                if (results.Count == 1)
                {
                    data["value"] = TruncateMessage(results[0].ToString(), 2000);
                }

                if (results.Count > 0)
                {
                    var matchStrings = results.Select(r => TruncateMessage(r.ToString(), 500)).ToList();
                    data["matches"] = string.Join("\n---\n", matchStrings);
                }
            }
            catch (Exception ex)
            {
                data["error"] = ex.Message;
                data["path"] = filePath;
                data["query"] = jsonPath;
            }

            return data;
        }

        private Dictionary<string, object> ExecuteXmlRule(GatherRule rule)
        {
            var data = new Dictionary<string, object>();
            var filePath = rule.Target;

            if (string.IsNullOrEmpty(filePath))
                return data;

            filePath = Environment.ExpandEnvironmentVariables(filePath);

            if (!GatherRuleGuards.IsFilePathAllowed(filePath, UnrestrictedMode))
                return EmitSecurityWarning(rule, "xml", filePath);

            string xpath;
            if (rule.Parameters == null || !rule.Parameters.TryGetValue("xpath", out xpath) ||
                string.IsNullOrEmpty(xpath))
            {
                _logger.Warning($"XML rule {rule.RuleId} has no 'xpath' parameter");
                data["error"] = "Missing required 'xpath' parameter";
                return data;
            }

            int maxResults = 20;
            string maxResultsStr;
            if (rule.Parameters.TryGetValue("maxResults", out maxResultsStr))
                int.TryParse(maxResultsStr, out maxResults);
            maxResults = Math.Min(maxResults, 100);

            try
            {
                if (!File.Exists(filePath))
                {
                    data["exists"] = false;
                    data["path"] = filePath;
                    return data;
                }

                var info = new FileInfo(filePath);
                if (info.Length > 200 * 1024) // 200 KB limit
                {
                    data["error"] = $"File too large ({info.Length} bytes, max 200 KB)";
                    data["path"] = filePath;
                    return data;
                }

                // Parse XML with DTD processing disabled (XXE prevention)
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                };

                XDocument doc;
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var xmlReader = XmlReader.Create(stream, settings))
                {
                    doc = XDocument.Load(xmlReader);
                }

                // Set up namespace resolver if namespaces are provided
                XmlNamespaceManager nsManager = null;
                string namespacesStr;
                if (rule.Parameters.TryGetValue("namespaces", out namespacesStr) &&
                    !string.IsNullOrEmpty(namespacesStr))
                {
                    nsManager = new XmlNamespaceManager(new NameTable());
                    foreach (var ns in namespacesStr.Split(';'))
                    {
                        var parts = ns.Split(new[] { '=' }, 2);
                        if (parts.Length == 2 && !string.IsNullOrEmpty(parts[0]) && !string.IsNullOrEmpty(parts[1]))
                        {
                            nsManager.AddNamespace(parts[0].Trim(), parts[1].Trim());
                        }
                    }
                }

                // Execute XPath — try element selection first, fall back to evaluate for scalar/attribute results
                var elements = nsManager != null
                    ? doc.XPathSelectElements(xpath, nsManager).Take(maxResults).ToList()
                    : doc.XPathSelectElements(xpath).Take(maxResults).ToList();

                data["path"] = filePath;
                data["query"] = xpath;

                if (elements.Count > 0)
                {
                    data["matchCount"] = elements.Count;
                    if (elements.Count == 1)
                    {
                        data["value"] = TruncateMessage(elements[0].ToString(), 2000);
                    }
                    var matchStrings = elements.Select(e => TruncateMessage(e.ToString(), 500)).ToList();
                    data["matches"] = string.Join("\n---\n", matchStrings);
                }
                else
                {
                    // Try XPathEvaluate for scalar results (attributes, text(), count(), etc.)
                    var evalResult = nsManager != null
                        ? doc.XPathEvaluate(xpath, nsManager)
                        : doc.XPathEvaluate(xpath);

                    if (evalResult is IEnumerable<object> enumerable)
                    {
                        var items = enumerable.Take(maxResults).ToList();
                        data["matchCount"] = items.Count;
                        if (items.Count == 1)
                        {
                            data["value"] = TruncateMessage(items[0].ToString(), 2000);
                        }
                        if (items.Count > 0)
                        {
                            data["matches"] = string.Join("\n---\n",
                                items.Select(i => TruncateMessage(i.ToString(), 500)));
                        }
                    }
                    else if (evalResult != null)
                    {
                        data["matchCount"] = 1;
                        data["value"] = TruncateMessage(evalResult.ToString(), 2000);
                    }
                    else
                    {
                        data["matchCount"] = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                data["error"] = ex.Message;
                data["path"] = filePath;
                data["query"] = xpath;
            }

            return data;
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

