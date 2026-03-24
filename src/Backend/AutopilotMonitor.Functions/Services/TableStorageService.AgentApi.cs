using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    public partial class TableStorageService
    {
        // ===== EVENT TYPE INDEX =====

        /// <summary>
        /// Upserts entries into the EventTypeIndex table for each distinct event type in the batch.
        /// PartitionKey = {tenantId}_{eventType}, RowKey = {invertedTicks}_{sessionId}
        /// This enables efficient "find all sessions with event X" queries.
        /// </summary>
        public async Task UpsertEventTypeIndexBatchAsync(string tenantId, string sessionId, IEnumerable<string> eventTypes)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.EventTypeIndex);
                var rowKey = $"{(DateTime.MaxValue.Ticks - DateTime.UtcNow.Ticks):D19}_{sessionId}";

                var tasks = eventTypes.Distinct().Select(eventType =>
                {
                    var partitionKey = $"{tenantId}_{eventType}";
                    var entity = new TableEntity(partitionKey, rowKey)
                    {
                        ["SessionId"] = sessionId,
                        ["TenantId"] = tenantId,
                        ["EventType"] = eventType,
                    };
                    return tableClient.UpsertEntityAsync(entity);
                });

                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to upsert EventTypeIndex for session {SessionId}", sessionId);
            }
        }

        // ===== DEVICE SNAPSHOT =====

        private static readonly HashSet<string> _deviceSnapshotEventTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "tpm_status",
            "autopilot_profile",
            "secureboot_status",
            "bitlocker_status",
            "hardware_spec",
            "network_interface_info",
            "aad_join_status"
        };

        /// <summary>
        /// Upserts a DeviceSnapshot entry for the session, merging device property fields.
        /// Existing fields are preserved (not overwritten) to maintain first-seen values.
        /// </summary>
        public async Task UpsertDeviceSnapshotAsync(string tenantId, string sessionId, IEnumerable<AutopilotMonitor.Shared.Models.EnrollmentEvent> events)
        {
            try
            {
                var relevantEvents = events.Where(e => _deviceSnapshotEventTypes.Contains(e.EventType)).ToList();
                if (relevantEvents.Count == 0) return;

                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.DeviceSnapshot);

                TableEntity entity;
                try
                {
                    var existing = await tableClient.GetEntityAsync<TableEntity>(tenantId, sessionId);
                    entity = existing.Value;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    entity = new TableEntity(tenantId, sessionId)
                    {
                        ["SessionId"] = sessionId,
                        ["TenantId"] = tenantId,
                    };
                }

                void SetIfMissing(string key, object? value)
                {
                    if (value == null) return;
                    if (!entity.ContainsKey(key) || entity[key] == null)
                        entity[key] = value;
                }

                foreach (var evt in relevantEvents)
                {
                    var data = evt.Data;
                    if (data == null) continue;

                    try
                    {
                        switch (evt.EventType.ToLowerInvariant())
                        {
                            case "tpm_status":
                                SetIfMissing("TpmSpecVersion", SafeGetString(data, "specVersion"));
                                SetIfMissing("TpmManufacturer", SafeGetString(data, "manufacturerName"));
                                SetIfMissing("TpmActivated", SafeGetBool(data, "isActivated"));
                                SetIfMissing("TpmEnabled", SafeGetBool(data, "isEnabled"));
                                break;

                            case "autopilot_profile":
                                SetIfMissing("AutopilotMode", SafeGetString(data, "autopilotModeLabel"));
                                SetIfMissing("DomainJoinMethod", SafeGetString(data, "domainJoinMethodLabel"));
                                SetIfMissing("EspEnabled", SafeGetBool(data, "CloudAssignedEspEnabled"));
                                break;

                            case "secureboot_status":
                                SetIfMissing("SecureBootEnabled", SafeGetBool(data, "uefiSecureBootEnabled"));
                                break;

                            case "bitlocker_status":
                                SetIfMissing("BitlockerEnabled", SafeGetBool(data, "systemDriveProtected"));
                                break;

                            case "hardware_spec":
                                SetIfMissing("CpuName", SafeGetString(data, "cpuName"));
                                SetIfMissing("RamTotalGB", SafeGetDouble(data, "ramTotalGB"));
                                SetIfMissing("DiskCount", SafeGetInt(data, "diskCount"));
                                SetIfMissing("HasSSD", DetectHasSSD(data));
                                break;

                            case "network_interface_info":
                                SetIfMissing("ConnectionType", SafeGetString(data, "connectionType"));
                                SetIfMissing("LinkSpeedMbps", SafeGetInt(data, "linkSpeedMbps"));
                                break;

                            case "aad_join_status":
                                SetIfMissing("AadJoinType", SafeGetString(data, "joinType"));
                                break;
                        }
                    }
                    catch (Exception evtEx)
                    {
                        _logger.LogDebug(evtEx, "DeviceSnapshot: error processing event type {EventType}", evt.EventType);
                    }
                }

                await tableClient.UpsertEntityAsync(entity);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to upsert DeviceSnapshot for session {SessionId}", sessionId);
            }
        }

        private static string? SafeGetString(Dictionary<string, object> data, string key)
        {
            if (!data.TryGetValue(key, out var val)) return null;
            return val?.ToString();
        }

        private static bool? SafeGetBool(Dictionary<string, object> data, string key)
        {
            if (!data.TryGetValue(key, out var val) || val == null) return null;
            if (val is bool b) return b;
            if (bool.TryParse(val.ToString(), out var parsed)) return parsed;
            return null;
        }

        private static double? SafeGetDouble(Dictionary<string, object> data, string key)
        {
            if (!data.TryGetValue(key, out var val) || val == null) return null;
            try { return Convert.ToDouble(val); } catch { return null; }
        }

        private static int? SafeGetInt(Dictionary<string, object> data, string key)
        {
            if (!data.TryGetValue(key, out var val) || val == null) return null;
            try { return Convert.ToInt32(val); } catch { return null; }
        }

        private static bool? DetectHasSSD(Dictionary<string, object> data)
        {
            try
            {
                if (!data.TryGetValue("disks", out var disksObj) || disksObj == null) return null;
                if (disksObj is not System.Collections.IEnumerable diskList) return null;

                foreach (var disk in diskList)
                {
                    Dictionary<string, object>? diskDict = null;
                    if (disk is Dictionary<string, object> d)
                        diskDict = d;

                    if (diskDict == null) continue;

                    if (diskDict.TryGetValue("mediaType", out var mt))
                    {
                        var mtStr = mt?.ToString() ?? "";
                        if (mtStr.Equals("SSD", StringComparison.OrdinalIgnoreCase) ||
                            mtStr.Equals("NVMe", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
                return false;
            }
            catch
            {
                return null;
            }
        }

        // ===== CVE INDEX =====

        /// <summary>
        /// Upserts CVE index entries so sessions can be searched by CVE identifier.
        /// PartitionKey = {tenantId}_{cveId}, RowKey = sessionId
        /// Uses individual parallel upserts (not batch transactions) because PK differs per CVE.
        /// </summary>
        public async Task UpsertCveIndexEntriesAsync(string tenantId, string sessionId, List<Dictionary<string, object>> findings)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.CveIndex);
                var tasks = new List<Task>();

                foreach (var finding in findings)
                {
                    var softwareName = finding.TryGetValue("softwareName", out var sn) ? sn?.ToString() ?? "" : "";
                    var overallRisk = finding.TryGetValue("riskLevel", out var rl) ? rl?.ToString() ?? "" : "";

                    if (!finding.TryGetValue("vulnerabilities", out var vulnsObj) || vulnsObj == null) continue;
                    if (vulnsObj is not System.Collections.IEnumerable vulnList) continue;

                    foreach (var vulnObj in vulnList)
                    {
                        Dictionary<string, object>? vuln = null;
                        if (vulnObj is Dictionary<string, object> vd)
                            vuln = vd;

                        if (vuln == null) continue;

                        var cveId = vuln.TryGetValue("cveId", out var cid) ? cid?.ToString() : null;
                        if (string.IsNullOrEmpty(cveId)) continue;

                        var partitionKey = $"{tenantId}_{cveId}";
                        double cvssScore = 0;
                        try { if (vuln.TryGetValue("cvssScore", out var cs) && cs != null) cvssScore = Convert.ToDouble(cs); } catch { }

                        var cvssSeverity = vuln.TryGetValue("cvssSeverity", out var csvs) ? csvs?.ToString() ?? "" : "";
                        bool isKev = false;
                        try { if (vuln.TryGetValue("isKev", out var ik) && ik is bool ikb) isKev = ikb; } catch { }

                        var entity = new TableEntity(partitionKey, sessionId)
                        {
                            ["SessionId"] = sessionId,
                            ["TenantId"] = tenantId,
                            ["CveId"] = cveId,
                            ["SoftwareName"] = softwareName,
                            ["CvssScore"] = cvssScore,
                            ["CvssSeverity"] = cvssSeverity,
                            ["IsKev"] = isKev,
                            ["OverallRisk"] = overallRisk,
                            ["DetectedAt"] = DateTime.UtcNow,
                        };

                        tasks.Add(tableClient.UpsertEntityAsync(entity));
                    }
                }

                if (tasks.Count > 0)
                    await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to upsert CveIndex entries for session {SessionId}", sessionId);
            }
        }

        // ===== SEARCH METHODS =====

        /// <summary>
        /// Searches enrollment sessions by filter. Uses DeviceSnapshot index for hardware filters,
        /// otherwise scans Sessions table with OData filtering.
        /// </summary>
        public async Task<List<SessionSummary>> SearchSessionsAsync(string? tenantId, SessionSearchFilter filter)
        {
            if (filter.HasDeviceSnapshotFilters)
                return await SearchSessionsByDeviceSnapshotAsync(tenantId, filter);
            else
                return await SearchSessionsByScanAsync(tenantId, filter);
        }

        private async Task<List<SessionSummary>> SearchSessionsByDeviceSnapshotAsync(string? tenantId, SessionSearchFilter filter)
        {
            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.DeviceSnapshot);
            var oDataFilter = string.IsNullOrEmpty(tenantId) ? null : $"PartitionKey eq '{tenantId}'";

            var sessionIds = new List<string>();
            await foreach (var entity in tableClient.QueryAsync<TableEntity>(filter: oDataFilter))
            {
                // Apply in-memory filters for DeviceSnapshot fields
                if (filter.TpmSpecVersion != null && entity.GetString("TpmSpecVersion") != filter.TpmSpecVersion) continue;
                if (filter.TpmActivated.HasValue && entity.GetBoolean("TpmActivated") != filter.TpmActivated.Value) continue;
                if (filter.SecureBootEnabled.HasValue && entity.GetBoolean("SecureBootEnabled") != filter.SecureBootEnabled.Value) continue;
                if (filter.BitlockerEnabled.HasValue && entity.GetBoolean("BitlockerEnabled") != filter.BitlockerEnabled.Value) continue;
                if (filter.AutopilotMode != null && entity.GetString("AutopilotMode") != filter.AutopilotMode) continue;
                if (filter.DomainJoinMethod != null && entity.GetString("DomainJoinMethod") != filter.DomainJoinMethod) continue;
                if (filter.ConnectionType != null && entity.GetString("ConnectionType") != filter.ConnectionType) continue;
                if (filter.MinRamGB.HasValue)
                {
                    double? ram = null;
                    if (entity.TryGetValue("RamTotalGB", out var ramObj) && ramObj != null)
                    {
                        try { ram = Convert.ToDouble(ramObj); } catch { }
                    }
                    if (ram == null || ram < filter.MinRamGB.Value) continue;
                }
                if (filter.HasSSD.HasValue && entity.GetBoolean("HasSSD") != filter.HasSSD.Value) continue;

                sessionIds.Add(entity.RowKey); // RowKey = sessionId in DeviceSnapshot
                if (sessionIds.Count >= filter.Limit * 3) break; // over-fetch to allow for missing sessions
            }

            if (sessionIds.Count == 0) return new List<SessionSummary>();

            // Batch-get SessionSummaries from Sessions table
            var sessions = await BatchGetSessionsAsync(tenantId, sessionIds);

            // Apply any additional basic filters
            sessions = ApplyBasicFilters(sessions, filter);

            return sessions.Take(filter.Limit).ToList();
        }

        private async Task<List<SessionSummary>> SearchSessionsByScanAsync(string? tenantId, SessionSearchFilter filter)
        {
            var indexTableClient = _tableServiceClient.GetTableClient(Constants.TableNames.SessionsIndex);

            var filterParts = new List<string>();
            if (!string.IsNullOrEmpty(tenantId))
                filterParts.Add($"PartitionKey eq '{tenantId}'");
            if (!string.IsNullOrEmpty(filter.Status))
                filterParts.Add($"Status eq '{filter.Status}'");
            if (!string.IsNullOrEmpty(filter.Manufacturer))
                filterParts.Add($"Manufacturer eq '{filter.Manufacturer}'");
            if (!string.IsNullOrEmpty(filter.Model))
                filterParts.Add($"Model eq '{filter.Model}'");
            if (!string.IsNullOrEmpty(filter.EnrollmentType))
                filterParts.Add($"EnrollmentType eq '{filter.EnrollmentType}'");
            if (!string.IsNullOrEmpty(filter.DeviceName))
                filterParts.Add($"DeviceName ge '{filter.DeviceName}' and DeviceName lt '{filter.DeviceName}~'");
            if (!string.IsNullOrEmpty(filter.OsBuild))
                filterParts.Add($"OsBuild ge '{filter.OsBuild}' and OsBuild lt '{filter.OsBuild}~'");

            var oDataFilter = filterParts.Count > 0 ? string.Join(" and ", filterParts) : null;

            var sessions = new List<SessionSummary>();
            await foreach (var entity in indexTableClient.QueryAsync<TableEntity>(filter: oDataFilter))
            {
                var session = MapIndexEntityToSessionSummary(entity);

                // Client-side filters
                if (!string.IsNullOrEmpty(filter.SerialNumber) &&
                    !string.Equals(session.SerialNumber, filter.SerialNumber, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (filter.IsPreProvisioned.HasValue && session.IsPreProvisioned != filter.IsPreProvisioned.Value) continue;
                if (filter.IsHybridJoin.HasValue && session.IsHybridJoin != filter.IsHybridJoin.Value) continue;
                if (!string.IsNullOrEmpty(filter.GeoCountry) &&
                    !string.Equals(session.GeoCountry, filter.GeoCountry, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (filter.StartedAfter.HasValue && session.StartedAt < filter.StartedAfter.Value) continue;
                if (filter.StartedBefore.HasValue && session.StartedAt > filter.StartedBefore.Value) continue;
                if (!string.IsNullOrEmpty(filter.AgentVersion) &&
                    !string.Equals(session.AgentVersion, filter.AgentVersion, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrEmpty(filter.ImeAgentVersion) &&
                    !string.Equals(session.ImeAgentVersion, filter.ImeAgentVersion, StringComparison.OrdinalIgnoreCase))
                    continue;

                sessions.Add(session);
                if (sessions.Count >= filter.Limit) break;
            }

            return sessions;
        }

        private List<SessionSummary> ApplyBasicFilters(List<SessionSummary> sessions, SessionSearchFilter filter)
        {
            return sessions.Where(s =>
            {
                if (!string.IsNullOrEmpty(filter.Status) && s.Status.ToString() != filter.Status) return false;
                if (!string.IsNullOrEmpty(filter.SerialNumber) &&
                    !string.Equals(s.SerialNumber, filter.SerialNumber, StringComparison.OrdinalIgnoreCase)) return false;
                if (filter.IsPreProvisioned.HasValue && s.IsPreProvisioned != filter.IsPreProvisioned.Value) return false;
                if (filter.IsHybridJoin.HasValue && s.IsHybridJoin != filter.IsHybridJoin.Value) return false;
                if (!string.IsNullOrEmpty(filter.GeoCountry) &&
                    !string.Equals(s.GeoCountry, filter.GeoCountry, StringComparison.OrdinalIgnoreCase)) return false;
                if (filter.StartedAfter.HasValue && s.StartedAt < filter.StartedAfter.Value) return false;
                if (filter.StartedBefore.HasValue && s.StartedAt > filter.StartedBefore.Value) return false;
                if (!string.IsNullOrEmpty(filter.AgentVersion) &&
                    !string.Equals(s.AgentVersion, filter.AgentVersion, StringComparison.OrdinalIgnoreCase)) return false;
                if (!string.IsNullOrEmpty(filter.ImeAgentVersion) &&
                    !string.Equals(s.ImeAgentVersion, filter.ImeAgentVersion, StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }).ToList();
        }

        private async Task<List<SessionSummary>> BatchGetSessionsAsync(string? tenantId, List<string> sessionIds)
        {
            var sessionsTableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
            var semaphore = new System.Threading.SemaphoreSlim(20, 20);

            var tasks = sessionIds.Select(async sessionId =>
            {
                await semaphore.WaitAsync();
                try
                {
                    // Try to determine the tenantId for this session
                    var partitionKey = tenantId ?? string.Empty;
                    if (string.IsNullOrEmpty(partitionKey))
                    {
                        // Cross-tenant: scan SessionsIndex for this sessionId
                        var indexTableClient = _tableServiceClient.GetTableClient(Constants.TableNames.SessionsIndex);
                        await foreach (var idxEntity in indexTableClient.QueryAsync<TableEntity>(
                            filter: $"SessionId eq '{sessionId}'",
                            maxPerPage: 1))
                        {
                            return MapIndexEntityToSessionSummary(idxEntity);
                        }
                        return null;
                    }

                    var response = await sessionsTableClient.GetEntityAsync<TableEntity>(partitionKey, sessionId);
                    return MapToSessionSummary(response.Value);
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    return null;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to get session {SessionId}", sessionId);
                    return null;
                }
                finally
                {
                    semaphore.Release();
                }
            });

            var results = await Task.WhenAll(tasks);
            return results.Where(s => s != null).Select(s => s!).ToList();
        }

        /// <summary>
        /// Searches sessions that contain a specific event type using the EventTypeIndex.
        /// Note: source/severity/phase filtering is not supported in v1 (too expensive).
        /// </summary>
        public async Task<List<SessionSummary>> SearchSessionsByEventAsync(
            string? tenantId, string eventType, string? source, string? severity, string? phase, int limit = 50)
        {
            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.EventTypeIndex);

            string? oDataFilter;
            if (!string.IsNullOrEmpty(tenantId))
                oDataFilter = $"PartitionKey eq '{tenantId}_{eventType}'";
            else
                oDataFilter = null; // full scan — not efficient but functional

            var sessionIds = new List<string>();
            await foreach (var entity in tableClient.QueryAsync<TableEntity>(filter: oDataFilter))
            {
                // For cross-tenant search, filter by eventType in the PartitionKey
                if (string.IsNullOrEmpty(tenantId))
                {
                    var pk = entity.PartitionKey;
                    var underscoreIdx = pk.LastIndexOf('_');
                    if (underscoreIdx < 0) continue;
                    var pkEventType = pk.Substring(underscoreIdx + 1);
                    if (!string.Equals(pkEventType, eventType, StringComparison.OrdinalIgnoreCase)) continue;
                }

                var sessionId = entity.GetString("SessionId");
                if (!string.IsNullOrEmpty(sessionId) && !sessionIds.Contains(sessionId))
                    sessionIds.Add(sessionId);

                if (sessionIds.Count >= limit * 2) break;
            }

            if (sessionIds.Count == 0) return new List<SessionSummary>();

            var sessions = await BatchGetSessionsAsync(tenantId, sessionIds);
            return sessions.Take(limit).ToList();
        }

        /// <summary>
        /// Searches sessions affected by a specific CVE using the CveIndex.
        /// </summary>
        public async Task<List<SessionSummary>> SearchSessionsByCveAsync(
            string? tenantId, string cveId, double? minCvssScore, string? overallRisk, int limit = 50)
        {
            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.CveIndex);

            string oDataFilter;
            if (!string.IsNullOrEmpty(tenantId))
                oDataFilter = $"PartitionKey eq '{tenantId}_{cveId}'";
            else
                oDataFilter = $"PartitionKey ge '{cveId}' and PartitionKey lt '{cveId}~'";

            var sessionIds = new List<string>();
            await foreach (var entity in tableClient.QueryAsync<TableEntity>(filter: oDataFilter))
            {
                if (minCvssScore.HasValue)
                {
                    var score = entity.GetDouble("CvssScore");
                    if (score == null || score < minCvssScore.Value) continue;
                }
                if (!string.IsNullOrEmpty(overallRisk))
                {
                    var risk = entity.GetString("OverallRisk");
                    if (!string.Equals(risk, overallRisk, StringComparison.OrdinalIgnoreCase)) continue;
                }

                var sessionId = entity.GetString("SessionId") ?? entity.RowKey;
                if (!string.IsNullOrEmpty(sessionId) && !sessionIds.Contains(sessionId))
                    sessionIds.Add(sessionId);

                if (sessionIds.Count >= limit * 2) break;
            }

            if (sessionIds.Count == 0) return new List<SessionSummary>();

            // Extract tenantId from PartitionKey for cross-tenant lookup if needed
            var sessions = await BatchGetSessionsAsync(tenantId, sessionIds);
            return sessions.Take(limit).ToList();
        }

        /// <summary>
        /// Returns aggregated session metrics grouped by tenant.
        /// </summary>
        public async Task<List<object>> GetMetricsSummaryAsync(string? tenantId)
        {
            var indexTableClient = _tableServiceClient.GetTableClient(Constants.TableNames.SessionsIndex);
            var oDataFilter = string.IsNullOrEmpty(tenantId) ? null : $"PartitionKey eq '{tenantId}'";

            var groups = new Dictionary<string, (int total, int succeeded, int failed, int inProgress)>(StringComparer.OrdinalIgnoreCase);

            await foreach (var entity in indexTableClient.QueryAsync<TableEntity>(filter: oDataFilter))
            {
                var pk = entity.PartitionKey;
                var statusStr = entity.GetString("Status") ?? "InProgress";

                if (!groups.TryGetValue(pk, out var g))
                    g = (0, 0, 0, 0);

                var total = g.total + 1;
                var succeeded = g.succeeded + (statusStr == "Succeeded" ? 1 : 0);
                var failed = g.failed + (statusStr == "Failed" ? 1 : 0);
                var inProg = g.inProgress + (statusStr == "InProgress" ? 1 : 0);
                groups[pk] = (total, succeeded, failed, inProg);
            }

            return groups.Select(kvp => (object)new
            {
                tenantId = kvp.Key,
                totalSessions = kvp.Value.total,
                succeeded = kvp.Value.succeeded,
                failed = kvp.Value.failed,
                inProgress = kvp.Value.inProgress,
                failureRate = kvp.Value.total > 0
                    ? Math.Round((double)kvp.Value.failed / kvp.Value.total * 100, 1)
                    : 0.0
            }).ToList();
        }
    }
}
