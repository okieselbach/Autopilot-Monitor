using System;
using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime
{
    /// <summary>
    /// Consumes the three self-update marker files written by SelfUpdater on every agent startup
    /// (updated / skipped / checked) and converts them into a single <c>agent_version_check</c>
    /// event with an <c>outcome</c> discriminator. Applies session-scoped dedup for the
    /// up_to_date outcome so repeated reboots in the same session don't spam the timeline.
    ///
    /// All filesystem paths are injectable so this can be exercised in unit tests without touching
    /// %ProgramData%.
    /// </summary>
    public static class VersionCheckEventBuilder
    {
        public sealed class Paths
        {
            public string UpdatedMarker { get; set; }
            public string SkippedMarker { get; set; }
            public string CheckedMarker { get; set; }
            public string LastEmit      { get; set; }

            public static Paths Default() => new Paths
            {
                UpdatedMarker = Environment.ExpandEnvironmentVariables(Constants.SelfUpdateMarkerFile),
                SkippedMarker = Environment.ExpandEnvironmentVariables(Constants.SelfUpdateSkippedMarkerFile),
                CheckedMarker = Environment.ExpandEnvironmentVariables(Constants.SelfUpdateCheckedMarkerFile),
                LastEmit      = Environment.ExpandEnvironmentVariables(Constants.LastVersionCheckEmitFile)
            };
        }

        public sealed class BuildResult
        {
            /// <summary>Event to emit; null when nothing to report (no marker) or when deduped.</summary>
            public EnrollmentEvent Event { get; set; }

            /// <summary>True when a marker was read but the event was suppressed by dedup.</summary>
            public bool Deduped { get; set; }

            /// <summary>Outcome that was detected (even if deduped), for logging. Null when no marker.</summary>
            public string Outcome { get; set; }

            /// <summary>Human-readable message for warning logs when a marker file was malformed.</summary>
            public string ParseError { get; set; }
        }

        /// <summary>
        /// Reads available markers, applies dedup, updates the last-emit file and deletes consumed markers.
        /// Returns the event to emit (or null). Never throws — errors are returned via ParseError.
        ///
        /// Priority: Updated > Skipped > Checked. If more than one marker exists (rare — stale files
        /// from a crashed run), the highest-priority one wins and the others are deleted defensively.
        /// </summary>
        public static BuildResult TryBuild(
            string sessionId,
            string tenantId,
            DateTime agentStartTimeUtc,
            Paths paths = null)
        {
            paths = paths ?? Paths.Default();
            var result = new BuildResult();

            try
            {
                // Priority order — once consumed, all three are deleted.
                if (File.Exists(paths.UpdatedMarker))
                {
                    result.Outcome = "updated";
                    result.Event = BuildUpdatedEvent(paths.UpdatedMarker, sessionId, tenantId, agentStartTimeUtc);
                }
                else if (File.Exists(paths.SkippedMarker))
                {
                    var skipEvent = BuildSkippedEvent(paths.SkippedMarker, sessionId, tenantId, out var skipOutcome);
                    result.Outcome = skipOutcome;

                    // Session-scoped dedup for downgrade_blocked: the runtime hash-mismatch path
                    // re-fires on every agent restart, and each restart would otherwise emit another
                    // Warning event for the same current→latest pair. Suppress duplicates within the
                    // same session when the same downgrade is advertised by the backend.
                    if (string.Equals(skipOutcome, "downgrade_blocked", StringComparison.Ordinal))
                    {
                        var latestVersion = ExtractLatestVersion(skipEvent);
                        var lastEmit = TryReadLastEmit(paths.LastEmit);
                        if (lastEmit != null &&
                            string.Equals(lastEmit.SessionId, sessionId, StringComparison.Ordinal) &&
                            string.Equals(lastEmit.LatestVersion, latestVersion, StringComparison.Ordinal) &&
                            string.Equals(lastEmit.Outcome, "downgrade_blocked", StringComparison.Ordinal))
                        {
                            result.Deduped = true;
                        }
                        else
                        {
                            result.Event = skipEvent;
                        }
                    }
                    else
                    {
                        result.Event = skipEvent;
                    }
                }
                else if (File.Exists(paths.CheckedMarker))
                {
                    result.Outcome = "up_to_date";
                    var marker = ReadMarker(paths.CheckedMarker);
                    var latestVersion = (string)marker["latestVersion"] ?? "unknown";

                    // Session-scoped dedup: suppress repeated up_to_date events for the same
                    // latestVersion within the same session. Update/Skip/Failure are never deduped.
                    var lastEmit = TryReadLastEmit(paths.LastEmit);
                    if (lastEmit != null &&
                        string.Equals(lastEmit.SessionId, sessionId, StringComparison.Ordinal) &&
                        string.Equals(lastEmit.LatestVersion, latestVersion, StringComparison.Ordinal) &&
                        string.Equals(lastEmit.Outcome, "up_to_date", StringComparison.Ordinal))
                    {
                        result.Deduped = true;
                    }
                    else
                    {
                        result.Event = BuildUpToDateEvent(marker, sessionId, tenantId);
                    }
                }
                else
                {
                    return result; // no marker — nothing to do
                }
            }
            catch (Exception ex)
            {
                result.ParseError = ex.Message;
            }

            // Persist the last-emit state before cleanup so a crash right after emit doesn't re-emit.
            if (result.Event != null)
            {
                var latestVersion = ExtractLatestVersion(result.Event);
                TryWriteLastEmit(paths.LastEmit, sessionId, latestVersion, result.Outcome);
            }

            // Always delete markers once consumed (or attempted) — stale markers would cause loops.
            TryDelete(paths.UpdatedMarker);
            TryDelete(paths.SkippedMarker);
            TryDelete(paths.CheckedMarker);

            return result;
        }

        // ── Individual builders ─────────────────────────────────────────────

        private static EnrollmentEvent BuildUpdatedEvent(string path, string sessionId, string tenantId, DateTime agentStartTimeUtc)
        {
            var marker = ReadMarker(path);

            var previousVersion = (string)marker["previousVersion"] ?? "unknown";
            var newVersion      = (string)marker["newVersion"] ?? "unknown";
            var updatedAtUtc    = (string)marker["updatedAtUtc"] ?? "unknown";
            var triggerReason   = (string)marker["triggerReason"] ?? "startup";
            var exitAtUtc       = (string)marker["exitAtUtc"];

            long? downtimeMs = null;
            if (!string.IsNullOrEmpty(exitAtUtc) &&
                DateTime.TryParse(exitAtUtc, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var exitDt))
            {
                downtimeMs = (long)(agentStartTimeUtc - exitDt).TotalMilliseconds;
            }

            var data = new Dictionary<string, object>
            {
                { "outcome", "updated" },
                { "previousVersion", previousVersion },
                { "newVersion", newVersion },
                { "latestVersion", newVersion },
                { "updatedAtUtc", updatedAtUtc },
                { "triggerReason", triggerReason }
            };
            if (exitAtUtc != null)                       data["exitAtUtc"]      = exitAtUtc;
            CopyLong(marker, "versionCheckMs", data);
            CopyLong(marker, "downloadMs", data);
            CopyLong(marker, "zipSizeBytes", data);
            CopyLong(marker, "verifyMs", data);
            CopyLong(marker, "extractMs", data);
            CopyLong(marker, "swapMs", data);
            CopyLong(marker, "totalUpdateMs", data);
            if (downtimeMs.HasValue)                     data["downtimeMs"]     = downtimeMs.Value;

            var severity = string.Equals(triggerReason, "runtime_hash_mismatch", StringComparison.Ordinal)
                ? EventSeverity.Warning
                : EventSeverity.Info;

            return new EnrollmentEvent
            {
                SessionId = sessionId,
                TenantId = tenantId,
                EventType = Constants.EventTypes.AgentVersionCheck,
                Severity = severity,
                Source = "Agent",
                Message = $"Agent self-updated from {previousVersion} to {newVersion} (trigger={triggerReason})",
                Data = data,
                ImmediateUpload = true
            };
        }

        private static EnrollmentEvent BuildSkippedEvent(string path, string sessionId, string tenantId, out string outcome)
        {
            var marker = ReadMarker(path);

            var reason         = (string)marker["reason"] ?? "unknown";
            var currentVersion = (string)marker["currentVersion"] ?? "unknown";
            var latestVersion  = (string)marker["latestVersion"];
            var skippedAtUtc   = (string)marker["skippedAtUtc"] ?? "unknown";
            var errorDetail    = (string)marker["errorDetail"] ?? string.Empty;
            var triggerReason  = (string)marker["triggerReason"];

            // Marker may already carry outcome (new SelfUpdater). For forward-compat with old markers
            // lying around across an upgrade, derive it from the reason.
            outcome = (string)marker["outcome"]
                      ?? (string.Equals(reason, "version_check_failed", StringComparison.Ordinal) ? "check_failed" : "skipped");

            var data = new Dictionary<string, object>
            {
                { "outcome", outcome },
                { "reason", reason },
                { "currentVersion", currentVersion },
                { "skippedAtUtc", skippedAtUtc },
                { "errorDetail", errorDetail }
            };
            if (latestVersion != null)
                data["latestVersion"] = latestVersion;
            if (!string.IsNullOrEmpty(triggerReason))
                data["triggerReason"] = triggerReason;

            string message;
            if (outcome == "check_failed")
                message = $"Agent version check failed (reason={reason})";
            else if (outcome == "downgrade_blocked")
                message = $"Agent self-update blocked: downgrade from {currentVersion} to {latestVersion ?? "unknown"} (trigger={triggerReason ?? "unknown"}, allowDowngrade=false)";
            else
                message = $"Agent self-update skipped (reason={reason})";

            return new EnrollmentEvent
            {
                SessionId = sessionId,
                TenantId = tenantId,
                EventType = Constants.EventTypes.AgentVersionCheck,
                Severity = EventSeverity.Warning,
                Source = "Agent",
                Message = message,
                Data = data,
                ImmediateUpload = true
            };
        }

        private static EnrollmentEvent BuildUpToDateEvent(JObject marker, string sessionId, string tenantId)
        {
            var currentVersion = (string)marker["currentVersion"] ?? "unknown";
            var latestVersion  = (string)marker["latestVersion"] ?? "unknown";
            var checkedAtUtc   = (string)marker["checkedAtUtc"] ?? "unknown";

            var data = new Dictionary<string, object>
            {
                { "outcome", "up_to_date" },
                { "currentVersion", currentVersion },
                { "latestVersion", latestVersion },
                { "checkedAtUtc", checkedAtUtc }
            };
            CopyLong(marker, "versionCheckMs", data);

            return new EnrollmentEvent
            {
                SessionId = sessionId,
                TenantId = tenantId,
                EventType = Constants.EventTypes.AgentVersionCheck,
                Severity = EventSeverity.Info,
                Source = "Agent",
                Message = $"Agent version check: up to date ({currentVersion})",
                Data = data,
                ImmediateUpload = true
            };
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private sealed class LastEmit
        {
            public string SessionId { get; set; }
            public string LatestVersion { get; set; }
            public string Outcome { get; set; }
        }

        private static JObject ReadMarker(string path) => JObject.Parse(File.ReadAllText(path));

        private static void CopyLong(JObject marker, string key, Dictionary<string, object> target)
        {
            var token = marker[key];
            if (token == null || token.Type == JTokenType.Null) return;
            try { target[key] = token.ToObject<long>(); } catch { /* skip malformed field */ }
        }

        private static string ExtractLatestVersion(EnrollmentEvent evt)
        {
            if (evt?.Data == null) return "unknown";
            return evt.Data.TryGetValue("latestVersion", out var v) && v != null ? v.ToString() : "unknown";
        }

        private static LastEmit TryReadLastEmit(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var obj = JObject.Parse(File.ReadAllText(path));
                return new LastEmit
                {
                    SessionId     = (string)obj["sessionId"],
                    LatestVersion = (string)obj["latestVersion"],
                    Outcome       = (string)obj["outcome"]
                };
            }
            catch { return null; }
        }

        private static void TryWriteLastEmit(string path, string sessionId, string latestVersion, string outcome)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var payload = new JObject
                {
                    ["sessionId"]      = sessionId ?? string.Empty,
                    ["latestVersion"]  = latestVersion ?? "unknown",
                    ["outcome"]        = outcome ?? "unknown",
                    ["emittedAtUtc"]   = DateTime.UtcNow.ToString("O")
                };
                File.WriteAllText(path, payload.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch { /* best-effort */ }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
