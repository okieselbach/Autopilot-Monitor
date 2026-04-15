using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AutopilotMonitor.Agent.Core.Monitoring.Tracking;
using AutopilotMonitor.Shared.Models;

using AutopilotMonitor.Agent.Core.Monitoring.Runtime;

namespace AutopilotMonitor.Agent.Core.Monitoring.Telemetry.Gather.Collectors
{
    public class LogParserCollector : IGatherRuleCollector
    {
        public string CollectorType => "logparser";

        /// <summary>
        /// Executes the log parser rule. Returns null because logparser emits events directly
        /// via context.OnEventCollected rather than returning a single result dictionary.
        /// </summary>
        public Dictionary<string, object> Execute(GatherRule rule, GatherRuleContext context)
        {
            var filePath = rule.Target;
            if (string.IsNullOrEmpty(filePath))
                return null;

            // Expand custom tokens (%LOGGED_ON_USER_PROFILE%) and standard environment variables
            var userProfilePath = UserProfileResolver.ContainsUserProfileToken(filePath)
                ? UserProfileResolver.GetLoggedOnUserProfilePath() : null;
            filePath = UserProfileResolver.ExpandCustomTokens(filePath);
            if (filePath == null)
                return null; // Token present but no user logged on — skip silently

            // Apply IME log path override if set
            if (!string.IsNullOrEmpty(context.ImeLogPathOverride))
            {
                var fileName = Path.GetFileName(filePath);
                filePath = Path.Combine(context.ImeLogPathOverride, fileName);
            }

            // Get parameters
            string patternStr;
            if (rule.Parameters == null || !rule.Parameters.TryGetValue("pattern", out patternStr) ||
                string.IsNullOrEmpty(patternStr))
            {
                context.Logger.Warning($"LogParser rule {rule.RuleId} has no 'pattern' parameter");
                return null;
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
                context.Logger.Warning($"LogParser rule {rule.RuleId} has invalid regex: {ex.Message}");
                return null;
            }

            // Resolve file paths — supports wildcards (* and ?) in the filename portion
            var resolvedPaths = ResolveLogPaths(filePath, rule.RuleId, context);
            if (resolvedPaths.Count == 0)
            {
                context.Logger.Debug($"LogParser rule {rule.RuleId}: no files found for: {filePath}");
                return null;
            }

            foreach (var resolvedPath in resolvedPaths)
            {
                ProcessLogFile(resolvedPath, rule, pattern, trackPosition, maxLines, isTextMode, context);
            }

            return null;
        }

        private static List<string> ResolveLogPaths(string filePath, string ruleId, GatherRuleContext context)
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
                context.Logger.Warning($"LogParser rule {ruleId}: wildcard expansion failed for {filePath}: {ex.Message}");
                return new List<string>();
            }
        }

        private static void ProcessLogFile(string filePath, GatherRule rule, Regex pattern,
            bool trackPosition, int maxLines, bool isTextMode, GatherRuleContext context)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                long startPosition = trackPosition
                    ? context.FilePositionTracker.GetSafePosition(filePath, fileInfo.Length)
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
                                var severity = GatherRuleContext.ParseSeverity(rule.OutputSeverity);

                                context.OnEventCollected(new EnrollmentEvent
                                {
                                    SessionId = context.SessionId,
                                    TenantId = context.TenantId,
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

                                context.OnEventCollected(new EnrollmentEvent
                                {
                                    SessionId = context.SessionId,
                                    TenantId = context.TenantId,
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
                    context.FilePositionTracker.SetPosition(filePath, endPosition);

                if (matchCount > 0)
                    context.Logger.Debug($"LogParser rule {rule.RuleId}: {matchCount} matches from {linesRead} lines in {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                context.Logger.Warning($"LogParser rule {rule.RuleId} failed reading {filePath}: {ex.Message}");
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
    }
}
