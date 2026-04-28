using System.Text.Json;
using System.Text.RegularExpressions;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Partial: Individual condition evaluators (event type, event data, event count,
    /// phase duration, app install duration, event correlation, confidence factors).
    /// </summary>
    public partial class RuleEngine
    {
        private (bool matched, object evidence) EvaluateCondition(RuleCondition condition, List<EnrollmentEvent> events)
        {
            switch (condition.Source)
            {
                case "event_type":
                    return EvaluateEventTypeCondition(condition, events);

                case "event_data":
                    return EvaluateEventDataCondition(condition, events);

                case "event_count":
                    return EvaluateEventCountCondition(condition, events);

                case "phase_duration":
                    return EvaluatePhaseDurationCondition(condition, events);

                case "app_install_duration":
                    return EvaluateAppInstallDurationCondition(condition, events);

                case "event_correlation":
                    return EvaluateEventCorrelationCondition(condition, events);

                default:
                    return (false, "unknown source");
            }
        }

        private (bool matched, object evidence) EvaluateEventTypeCondition(RuleCondition condition, List<EnrollmentEvent> events)
        {
            var matchingEvents = events.Where(e => MatchesEventType(e, condition.EventType)).ToList();

            if (!matchingEvents.Any())
                return (false, "no matching events");

            // If DataField is specified, check data within matching events
            if (!string.IsNullOrEmpty(condition.DataField))
            {
                foreach (var evt in matchingEvents)
                {
                    var fieldValue = GetDataFieldValue(evt, condition.DataField);
                    if (fieldValue != null && MatchesOperator(fieldValue, condition.Operator, condition.Value))
                    {
                        // Check suppressByEvent: skip this match if a resolving event exists
                        if (IsSuppressedByEvent(evt, condition.SuppressByEvent, events))
                            continue;

                        var evidence = new Dictionary<string, object>
                        {
                            ["eventId"] = evt.EventId,
                            ["sequence"] = evt.Sequence,
                            ["timestamp"] = evt.Timestamp,
                            ["eventType"] = evt.EventType,
                            ["field"] = condition.DataField,
                            ["value"] = fieldValue
                        };
                        AddDataFieldsToEvidence(evidence, evt, "", "");
                        return (true, evidence);
                    }
                }
                return (false, "data field not matched");
            }

            // Just check if event type exists — return first matching event for reference
            // Filter out suppressed events
            if (condition.SuppressByEvent != null)
            {
                matchingEvents = matchingEvents.Where(e => !IsSuppressedByEvent(e, condition.SuppressByEvent, events)).ToList();
                if (!matchingEvents.Any())
                    return (false, "all matches suppressed by resolving events");
            }

            var first = matchingEvents[0];
            var existsEvidence = new Dictionary<string, object>
            {
                ["eventId"] = first.EventId,
                ["sequence"] = first.Sequence,
                ["timestamp"] = first.Timestamp,
                ["eventType"] = condition.EventType,
                ["count"] = matchingEvents.Count
            };
            AddDataFieldsToEvidence(existsEvidence, first, "", "");
            return (true, existsEvidence);
        }

        private (bool matched, object evidence) EvaluateEventDataCondition(RuleCondition condition, List<EnrollmentEvent> events)
        {
            var matchingEvents = events.Where(e => MatchesEventType(e, condition.EventType)).ToList();

            foreach (var evt in matchingEvents)
            {
                var fieldValue = GetDataFieldValue(evt, condition.DataField);
                if (fieldValue != null && MatchesOperator(fieldValue, condition.Operator, condition.Value))
                {
                    var evidence = new Dictionary<string, object>
                    {
                        ["eventId"] = evt.EventId,
                        ["sequence"] = evt.Sequence,
                        ["timestamp"] = evt.Timestamp,
                        ["field"] = condition.DataField,
                        ["value"] = fieldValue
                    };
                    AddDataFieldsToEvidence(evidence, evt, "", "");
                    return (true, evidence);
                }
            }

            return (false, "no matching data");
        }

        private (bool matched, object evidence) EvaluateEventCountCondition(RuleCondition condition, List<EnrollmentEvent> events)
        {
            var matchingEvents = events.Where(e => MatchesEventType(e, condition.EventType)).ToList();

            // count_per_group_gte: group by DataField value (e.g. appId), fire if any group >= threshold
            if (condition.Operator == "count_per_group_gte" && !string.IsNullOrEmpty(condition.DataField) && int.TryParse(condition.Value, out var groupThreshold))
            {
                var groups = matchingEvents
                    .Select(e => new { Event = e, Key = GetDataFieldValue(e, condition.DataField) })
                    .Where(x => !string.IsNullOrEmpty(x.Key))
                    .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() >= groupThreshold)
                    .ToList();

                if (groups.Any())
                {
                    var worst = groups.OrderByDescending(g => g.Count()).First();
                    var firstEvent = worst.First().Event;
                    string groupKey = worst.Key ?? string.Empty;
                    string appName = GetDataFieldValue(firstEvent, "appName") ?? groupKey;
                    return (true, new Dictionary<string, object>
                    {
                        ["eventId"] = firstEvent.EventId ?? string.Empty,
                        ["sequence"] = firstEvent.Sequence,
                        ["timestamp"] = firstEvent.Timestamp,
                        ["groupKey"] = groupKey,
                        ["appName"] = appName,
                        ["count"] = worst.Count(),
                        ["threshold"] = groupThreshold
                    });
                }

                var distinctApps = matchingEvents
                    .Select(e => GetDataFieldValue(e, condition.DataField))
                    .Where(k => k != null)
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                return (false, new Dictionary<string, object> { ["totalEvents"] = matchingEvents.Count, ["distinctApps"] = distinctApps });
            }

            // count_gte: global count across all matching events
            var count = matchingEvents.Count;
            if (condition.Operator == "count_gte" && int.TryParse(condition.Value, out var threshold))
            {
                if (count >= threshold)
                {
                    var first = matchingEvents[0];
                    return (true, new Dictionary<string, object>
                    {
                        ["eventId"] = first.EventId,
                        ["sequence"] = first.Sequence,
                        ["timestamp"] = first.Timestamp,
                        ["count"] = count,
                        ["threshold"] = threshold
                    });
                }
            }

            return (false, new Dictionary<string, object> { ["count"] = count });
        }

        private static (bool matched, object evidence) EvaluatePhaseDurationCondition(RuleCondition condition, List<EnrollmentEvent> events)
        {
            // Find phase change events to calculate phase duration
            var phaseEvents = events
                .Where(e => e.EventType == "esp_phase_changed")
                .OrderBy(e => e.Timestamp)
                .ToList();

            if (!phaseEvents.Any())
                return (false, "no phase events");

            // condition.DataField = field to look up (e.g. "espPhase"), condition.Value = target phase name (e.g. "DeviceSetup")
            var targetPhase = condition.Value;
            var lookupField = string.IsNullOrEmpty(condition.DataField) ? "espPhase" : condition.DataField;

            for (int i = 0; i < phaseEvents.Count; i++)
            {
                var evt = phaseEvents[i];
                var currentPhase = evt.Data?.ContainsKey(lookupField) == true
                    ? evt.Data[lookupField]?.ToString()
                    : null;

                if (!string.Equals(currentPhase, targetPhase, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Calculate how long this phase lasted
                DateTime phaseEnd;
                string? phaseEndEventId = null;
                if (i + 1 < phaseEvents.Count)
                {
                    phaseEnd = phaseEvents[i + 1].Timestamp;
                    phaseEndEventId = phaseEvents[i + 1].EventId;
                }
                else
                {
                    phaseEnd = DateTime.UtcNow; // Phase is still active
                }

                var durationSeconds = (phaseEnd - evt.Timestamp).TotalSeconds;

                return (true, new Dictionary<string, object>
                {
                    ["eventId"] = evt.EventId,
                    ["sequence"] = evt.Sequence,
                    ["phaseStartTimestamp"] = evt.Timestamp,
                    ["phaseEndEventId"] = phaseEndEventId ?? "(still active)",
                    ["phase"] = targetPhase,
                    ["durationSeconds"] = durationSeconds,
                    ["durationFormatted"] = FormatDuration(durationSeconds)
                });
            }

            return (false, "phase not found");
        }

        private static string FormatDuration(double totalSeconds)
        {
            var ts = TimeSpan.FromSeconds(totalSeconds);
            if (ts.TotalHours >= 1)
                return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
            if (ts.TotalMinutes >= 1)
                return $"{ts.Minutes}m {ts.Seconds}s";
            return $"{ts.Seconds}s";
        }

        private bool EvaluateConfidenceFactor(ConfidenceFactor factor, List<EnrollmentEvent> events, Dictionary<string, object> matchedConditions)
        {
            if (string.IsNullOrEmpty(factor.Condition))
                return false;

            if (factor.Condition.StartsWith("phase_duration >"))
            {
                if (int.TryParse(factor.Condition.Replace("phase_duration >", "").Trim(), out var threshold))
                {
                    foreach (var mc in matchedConditions.Values)
                    {
                        if (mc is Dictionary<string, object> dict && dict.TryGetValue("durationSeconds", out var rawDuration))
                        {
                            var duration = Convert.ToDouble(rawDuration);
                            return duration > threshold;
                        }
                    }
                }
            }
            else if (factor.Condition == "exists")
            {
                return matchedConditions.ContainsKey(factor.Signal);
            }
            else if (factor.Condition.StartsWith("count >="))
            {
                if (int.TryParse(factor.Condition.Replace("count >=", "").Trim(), out var threshold))
                {
                    var count = events.Count(e => e.EventType == factor.Signal);
                    return count >= threshold;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks if a matched event is suppressed by a resolving event (e.g., app_install_completed
        /// suppresses app_install_failed when both share the same appId).
        /// </summary>
        private bool IsSuppressedByEvent(EnrollmentEvent matchedEvent, SuppressByEventConfig? config, List<EnrollmentEvent> allEvents)
        {
            if (config == null || string.IsNullOrEmpty(config.EventType) || string.IsNullOrEmpty(config.JoinField))
                return false;

            var joinValue = GetDataFieldValue(matchedEvent, config.JoinField);
            if (string.IsNullOrEmpty(joinValue))
                return false;

            return allEvents.Any(e =>
                MatchesEventType(e, config.EventType) &&
                string.Equals(GetDataFieldValue(e, config.JoinField), joinValue, StringComparison.OrdinalIgnoreCase));
        }

        // ===== HELPERS =====

        private bool MatchesEventType(EnrollmentEvent evt, string eventType)
        {
            if (string.IsNullOrEmpty(eventType))
                return false;

            return string.Equals(evt.EventType, eventType, StringComparison.OrdinalIgnoreCase);
        }

        private string? GetDataFieldValue(EnrollmentEvent evt, string dataField)
        {
            if (evt.Data == null || string.IsNullOrEmpty(dataField))
                return null;

            // Support checking message field directly
            if (dataField.Equals("message", StringComparison.OrdinalIgnoreCase))
                return evt.Message;

            // Flat lookup first — preserves the legacy contract for keys that contain a
            // literal dot (e.g. v1 events that wrote "scan_summary.critical_cves" as a
            // single key) and is the cheap path for the common case.
            if (TryFlatLookup(evt.Data, dataField, out var flat))
                return flat;

            // Dot-path traversal for nested Dictionary<string,object> / JsonElement values.
            // Used by rules that target a structured payload (e.g. `scan_summary.kev_matches`
            // when the emitter wrote `scan_summary` as a nested dict).
            if (dataField.IndexOf('.') >= 0)
                return ResolveDotPath(evt.Data, dataField);

            return null;
        }

        internal static bool TryFlatLookup(Dictionary<string, object> data, string key, out string? value)
        {
            if (data.TryGetValue(key, out var raw))
            {
                value = raw?.ToString();
                return true;
            }
            var hit = data.Keys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (hit != null)
            {
                value = data[hit]?.ToString();
                return true;
            }
            value = null;
            return false;
        }

        internal static string? ResolveDotPath(Dictionary<string, object> root, string path)
        {
            var parts = path.Split('.');
            object? current = root;
            foreach (var part in parts)
            {
                if (current == null) return null;

                switch (current)
                {
                    case IDictionary<string, object> dict:
                        if (!dict.TryGetValue(part, out var next))
                        {
                            var hit = dict.Keys.FirstOrDefault(k => k.Equals(part, StringComparison.OrdinalIgnoreCase));
                            if (hit == null) return null;
                            next = dict[hit];
                        }
                        current = next;
                        break;

                    case JsonElement jel when jel.ValueKind == JsonValueKind.Object:
                        if (jel.TryGetProperty(part, out var prop))
                        {
                            current = prop;
                        }
                        else
                        {
                            // Case-insensitive fallback for JsonElement.
                            JsonElement? found = null;
                            foreach (var p in jel.EnumerateObject())
                            {
                                if (p.Name.Equals(part, StringComparison.OrdinalIgnoreCase))
                                {
                                    found = p.Value;
                                    break;
                                }
                            }
                            if (found == null) return null;
                            current = found.Value;
                        }
                        break;

                    default:
                        // Scalar reached before path was exhausted — no descent possible.
                        return null;
                }
            }

            return current switch
            {
                null => null,
                JsonElement jel => jel.ValueKind == JsonValueKind.String ? jel.GetString() : jel.ToString(),
                _ => current.ToString(),
            };
        }

        private bool MatchesOperator(string fieldValue, string op, string compareValue)
        {
            switch (op?.ToLower())
            {
                case "equals":
                    return string.Equals(fieldValue, compareValue, StringComparison.OrdinalIgnoreCase);

                case "not_equals":
                    return !string.Equals(fieldValue, compareValue, StringComparison.OrdinalIgnoreCase);

                case "contains":
                    return fieldValue.IndexOf(compareValue, StringComparison.OrdinalIgnoreCase) >= 0;

                case "not_contains":
                    return fieldValue.IndexOf(compareValue, StringComparison.OrdinalIgnoreCase) < 0;

                case "regex":
                    try
                    {
                        return Regex.IsMatch(fieldValue, compareValue, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                    }
                    catch
                    {
                        return false;
                    }

                case "not_regex":
                    try
                    {
                        return !Regex.IsMatch(fieldValue, compareValue, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                    }
                    catch
                    {
                        return false;
                    }

                case "gt":
                    return double.TryParse(fieldValue, out var gt1) && double.TryParse(compareValue, out var gt2) && gt1 > gt2;

                case "lt":
                    return double.TryParse(fieldValue, out var lt1) && double.TryParse(compareValue, out var lt2) && lt1 < lt2;

                case "gte":
                    return double.TryParse(fieldValue, out var gte1) && double.TryParse(compareValue, out var gte2) && gte1 >= gte2;

                case "lte":
                    return double.TryParse(fieldValue, out var lte1) && double.TryParse(compareValue, out var lte2) && lte1 <= lte2;

                case "exists":
                    return !string.IsNullOrEmpty(fieldValue);

                case "not_exists":
                    return string.IsNullOrEmpty(fieldValue);

                case "in":
                    return MatchesInList(fieldValue, compareValue);

                case "not_in":
                    return !MatchesInList(fieldValue, compareValue);

                default:
                    return false;
            }
        }

        private static bool MatchesInList(string fieldValue, string compareValue)
        {
            if (string.IsNullOrEmpty(fieldValue) || string.IsNullOrEmpty(compareValue))
                return false;

            foreach (var entry in compareValue.Split(','))
            {
                var trimmed = entry.Trim();
                if (trimmed.Length == 0)
                    continue;
                if (string.Equals(fieldValue, trimmed, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private (bool matched, object evidence) EvaluateAppInstallDurationCondition(RuleCondition condition, List<EnrollmentEvent> events)
        {
            var sortedEvents = events.OrderBy(e => e.Timestamp).ThenBy(e => e.Sequence).ToList();

            var completionEventTypes = string.IsNullOrWhiteSpace(condition.EventType)
                ? new HashSet<string>(new[] { "app_install_completed", "app_install_failed" }, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(new[] { condition.EventType }, StringComparer.OrdinalIgnoreCase);

            var completionEvents = sortedEvents
                .Where(e => completionEventTypes.Contains(e.EventType ?? string.Empty))
                .ToList();

            foreach (var completionEvent in completionEvents)
            {
                var appId = GetDataFieldValue(completionEvent, "appId");
                var appName = GetDataFieldValue(completionEvent, "appName");
                var appKey = !string.IsNullOrWhiteSpace(appId) ? appId : appName;

                if (string.IsNullOrWhiteSpace(appKey))
                    continue;

                var startEvent = sortedEvents.LastOrDefault(e =>
                    (string.Equals(e.EventType, "app_install_started", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(e.EventType, "app_install_start", StringComparison.OrdinalIgnoreCase)) &&
                    e.Timestamp <= completionEvent.Timestamp &&
                    string.Equals(GetDataFieldValue(e, "appId") ?? GetDataFieldValue(e, "appName"), appKey, StringComparison.OrdinalIgnoreCase));

                if (startEvent == null)
                    continue;

                var durationSeconds = Math.Max(0, (completionEvent.Timestamp - startEvent.Timestamp).TotalSeconds);

                if (!MatchesOperator(durationSeconds.ToString(), condition.Operator, condition.Value))
                    continue;

                return (true, new Dictionary<string, object>
                {
                    ["eventId"] = completionEvent.EventId,
                    ["sequence"] = completionEvent.Sequence,
                    ["startEventId"] = startEvent.EventId,
                    ["startTimestamp"] = startEvent.Timestamp,
                    ["endTimestamp"] = completionEvent.Timestamp,
                    ["eventType"] = completionEvent.EventType,
                    ["appId"] = appId ?? string.Empty,
                    ["appName"] = appName ?? appKey,
                    ["durationSeconds"] = durationSeconds,
                    ["durationFormatted"] = FormatDuration(durationSeconds)
                });
            }

            return (false, "no app install duration matched");
        }

        /// <summary>
        /// Generic event correlation: finds pairs of events (A before B) sharing
        /// the same value in a join field, with optional filters on each event
        /// and optional time window constraint.
        ///
        /// Condition fields used:
        ///   EventType          = Event A type (required)
        ///   CorrelateEventType = Event B type (required)
        ///   JoinField          = field that must match in both events (required, e.g. "appId")
        ///   TimeWindowSeconds  = max seconds between A and B (optional, 0/null = unlimited)
        ///   DataField/Operator/Value       = optional filter on Event B
        ///   EventAFilterField/Operator/Value = optional filter on Event A
        /// </summary>
        private (bool matched, object evidence) EvaluateEventCorrelationCondition(RuleCondition condition, List<EnrollmentEvent> events)
        {
            if (string.IsNullOrEmpty(condition.EventType) ||
                string.IsNullOrEmpty(condition.CorrelateEventType) ||
                string.IsNullOrEmpty(condition.JoinField))
            {
                _logger.LogWarning($"event_correlation condition '{condition.Signal}' missing required fields (EventType, CorrelateEventType, JoinField)");
                return (false, "event_correlation requires EventType, CorrelateEventType, and JoinField");
            }

            var sortedEvents = events.OrderBy(e => e.Timestamp).ThenBy(e => e.Sequence).ToList();

            // Collect Event A candidates with optional filter
            var eventAList = sortedEvents
                .Where(e => MatchesEventType(e, condition.EventType))
                .Where(e =>
                {
                    if (string.IsNullOrEmpty(condition.EventAFilterField))
                        return true;
                    var val = GetDataFieldValue(e, condition.EventAFilterField);
                    return val != null && MatchesOperator(val, condition.EventAFilterOperator ?? "exists", condition.EventAFilterValue ?? "");
                })
                .ToList();

            // Collect Event B candidates with optional DataField/Operator/Value filter
            var eventBList = sortedEvents
                .Where(e => MatchesEventType(e, condition.CorrelateEventType))
                .Where(e =>
                {
                    if (string.IsNullOrEmpty(condition.DataField))
                        return true;
                    var val = GetDataFieldValue(e, condition.DataField);
                    return val != null && MatchesOperator(val, condition.Operator ?? "exists", condition.Value ?? "");
                })
                .ToList();

            if (!eventAList.Any() || !eventBList.Any())
                return (false, "no matching event pairs found");

            // Index Event A by join field value for O(1) lookup
            var eventAByJoinKey = eventAList
                .Select(e => new { Event = e, Key = GetDataFieldValue(e, condition.JoinField) })
                .Where(x => !string.IsNullOrEmpty(x.Key))
                .GroupBy(x => x.Key!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Event).ToList(), StringComparer.OrdinalIgnoreCase);

            // For each Event B, find a matching Event A (same join key, A before B, within time window)
            var matchedPairs = new List<Dictionary<string, object>>();
            var matchedJoinKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var eventB in eventBList)
            {
                var bKey = GetDataFieldValue(eventB, condition.JoinField);
                if (string.IsNullOrEmpty(bKey) || !eventAByJoinKey.ContainsKey(bKey))
                    continue;

                // Find the latest Event A that occurred before Event B
                var matchingA = eventAByJoinKey[bKey]
                    .Where(a => a.Timestamp < eventB.Timestamp ||
                                (a.Timestamp == eventB.Timestamp && a.Sequence < eventB.Sequence))
                    .Where(a =>
                    {
                        if (condition.TimeWindowSeconds == null || condition.TimeWindowSeconds <= 0)
                            return true;
                        return (eventB.Timestamp - a.Timestamp).TotalSeconds <= condition.TimeWindowSeconds.Value;
                    })
                    .OrderByDescending(a => a.Timestamp)
                    .ThenByDescending(a => a.Sequence)
                    .FirstOrDefault();

                if (matchingA == null)
                    continue;

                // One match per join key to avoid duplicates
                if (matchedJoinKeys.Contains(bKey))
                    continue;
                matchedJoinKeys.Add(bKey);

                // Build evidence for this correlated pair. Keep it slim — store only event
                // references (id/type/sequence/timestamp) plus the join key and a small set of
                // short identifying data fields. The UI lazy-loads the full event via /events/{id}
                // when the user expands the evidence row, so we don't need to embed Message or
                // multi-KB stack-traces here. This caps per-pair evidence at ~500 bytes and keeps
                // RuleResult.MatchedConditionsJson well below Table Storage's 64KB property limit
                // even when many pairs match (e.g. ANALYZE-APP-012 on long-running stuck sessions).
                var pair = new Dictionary<string, object>
                {
                    ["joinField"] = condition.JoinField,
                    ["joinValue"] = bKey,
                    ["eventA_eventId"] = matchingA.EventId ?? string.Empty,
                    ["eventA_eventType"] = matchingA.EventType ?? string.Empty,
                    ["eventA_timestamp"] = matchingA.Timestamp,
                    ["eventA_sequence"] = matchingA.Sequence,
                    ["eventB_eventId"] = eventB.EventId ?? string.Empty,
                    ["eventB_eventType"] = eventB.EventType ?? string.Empty,
                    ["eventB_timestamp"] = eventB.Timestamp,
                    ["eventB_sequence"] = eventB.Sequence,
                    ["timeDeltaSeconds"] = (eventB.Timestamp - matchingA.Timestamp).TotalSeconds
                };

                // Add slim identifying fields from both events (no free-text fields).
                AddDataFieldsToEvidence(pair, matchingA, "eventA_", condition.JoinField);
                AddDataFieldsToEvidence(pair, eventB, "eventB_", condition.JoinField);

                matchedPairs.Add(pair);
            }

            if (!matchedPairs.Any())
                return (false, "no correlated event pairs found");

            // Single match: flat dictionary; multiple: first as primary + capped allMatches list.
            // Cap protects against runaway evidence size — sessions with hundreds of correlated
            // pairs would otherwise produce MatchedConditionsJson > 64KB and fail Table Storage
            // persistence (see also defense-in-depth check in TableStorageService.StoreRuleResultAsync).
            //
            // CRITICAL: allMatches MUST be a list of cloned dictionaries, not the matchedPairs
            // list itself. matchedPairs[0] IS primary by reference — if we assigned the raw list,
            // primary["allMatches"][0] would point back to primary and Newtonsoft.Json would
            // throw "Self referencing loop detected" on the StoreRuleResultAsync UpsertEntity
            // serialize step. The flat clone via `new Dictionary<string, object>(p)` snapshots
            // each pair's data BEFORE primary["allMatches"] is set, so the clones never contain
            // the back-reference. Pre-existing latent bug from before slim-down — never fired
            // in production until a 12-day stuck session matched multiple correlation pairs.
            const int MaxAllMatches = 10;
            var primary = matchedPairs[0];
            primary["totalMatches"] = matchedPairs.Count;
            if (matchedPairs.Count > 1)
            {
                var capped = matchedPairs.Take(MaxAllMatches);
                primary["allMatches"] = capped.Select(p => new Dictionary<string, object>(p)).ToList();
                primary["matchesTruncated"] = matchedPairs.Count > MaxAllMatches;
            }
            return (true, primary);
        }

        /// <summary>
        /// Adds slim identifying data fields from an event to the evidence dictionary with a prefix.
        /// Free-text fields like <c>errorDetail</c> (often a stack trace) and <c>message</c> are
        /// deliberately excluded to keep <see cref="RuleResult.MatchedConditions"/> below Table
        /// Storage's 64KB property limit. UI fetches the full event on demand via /events/{id}.
        /// </summary>
        private void AddDataFieldsToEvidence(Dictionary<string, object> evidence, EnrollmentEvent evt, string prefix, string joinField)
        {
            if (evt.Data == null) return;

            // Whitelist: short identifiers only. errorDetail/message removed (can be multi-KB).
            var knownFields = new[] { "appId", "appName", "errorPatternId", "errorCode", "exitCode", "status" };

            foreach (var field in knownFields)
            {
                var val = GetDataFieldValue(evt, field);
                if (val != null)
                    evidence[$"{prefix}{field}"] = val;
            }
        }
    }
}

