using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Server→Agent action queueing. Actions live on the Sessions entity
    /// (PendingActionsJson column) so ingest can deliver them at zero extra I/O cost.
    /// </summary>
    public partial class TableStorageService
    {
        /// <summary>
        /// Appends an action to the session's pending-action queue. Dedups by action type:
        /// if an action with the same Type is already pending, it is replaced (last-write-wins
        /// on the reason/params, QueuedAt kept from the original so TTL doesn't reset).
        ///
        /// Uses ETag-based merge with a single retry on concurrency conflict.
        /// Safe to call multiple times for the same intent.
        /// </summary>
        public async Task<bool> QueueServerActionAsync(string tenantId, string sessionId, ServerAction action)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            if (string.IsNullOrWhiteSpace(action?.Type))
            {
                _logger.LogWarning($"QueueServerActionAsync: refusing to queue action with empty Type for session {sessionId}");
                return false;
            }

            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);

            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var entityResponse = await tableClient.GetEntityAsync<TableEntity>(tenantId, sessionId);
                    var entity = entityResponse.Value;

                    var existing = DeserializePendingActions(entity.GetString("PendingActionsJson"));
                    var existingQueuedAt = entity.GetDateTimeOffset("PendingActionsQueuedAt")?.UtcDateTime;

                    // Dedup by Type — replace any queued action of the same type with the new one.
                    // Preserve the earliest QueuedAt across the list so TTL doesn't reset on every re-queue.
                    existing.RemoveAll(a => string.Equals(a.Type, action!.Type, StringComparison.OrdinalIgnoreCase));
                    existing.Add(action!);

                    entity["PendingActionsJson"] = JsonConvert.SerializeObject(existing);
                    entity["PendingActionsQueuedAt"] = existingQueuedAt ?? action!.QueuedAt;

                    await tableClient.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Merge);
                    _logger.LogInformation($"Queued server action '{action!.Type}' for session {sessionId} (total pending: {existing.Count})");
                    return true;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    _logger.LogWarning($"QueueServerActionAsync: session {sessionId} not found for tenant {tenantId}");
                    return false;
                }
                catch (RequestFailedException ex) when (ex.Status == 412 && attempt == 0)
                {
                    // ETag mismatch — concurrent update. Retry once.
                    continue;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to queue server action '{action?.Type}' for session {sessionId}");
                    return false;
                }
            }

            _logger.LogWarning($"QueueServerActionAsync: ETag conflict persisted after retry for session {sessionId}");
            return false;
        }

        /// <summary>
        /// Reads and clears the pending-action queue for delivery via ingest response.
        ///
        /// Contract: at-least-once delivery. If a concurrent ingest clears the same batch
        /// (ETag conflict), we return the list we read but the clear did not succeed in this call.
        /// The agent must execute each action type idempotently regardless.
        ///
        /// Returns an empty list (no I/O beyond the required Get) when nothing is pending.
        /// </summary>
        public async Task<List<ServerAction>> FetchAndClearPendingActionsAsync(string tenantId, string sessionId)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);

            try
            {
                var entityResponse = await tableClient.GetEntityAsync<TableEntity>(tenantId, sessionId);
                var entity = entityResponse.Value;

                var actions = DeserializePendingActions(entity.GetString("PendingActionsJson"));
                if (actions.Count == 0)
                    return actions;

                // Clear via merge — on ETag conflict, treat as already-delivered (benign).
                try
                {
                    var clearUpdate = new TableEntity(tenantId, sessionId)
                    {
                        ["PendingActionsJson"] = string.Empty,
                        ["PendingActionsQueuedAt"] = (DateTime?)null
                    };
                    await tableClient.UpdateEntityAsync(clearUpdate, entity.ETag, TableUpdateMode.Merge);
                }
                catch (RequestFailedException ex) when (ex.Status == 412)
                {
                    _logger.LogDebug($"FetchAndClearPendingActionsAsync: ETag conflict clearing session {sessionId} — delivering read copy (at-least-once)");
                }

                return actions;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return new List<ServerAction>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to fetch/clear pending actions for session {sessionId}");
                return new List<ServerAction>();
            }
        }

        /// <summary>
        /// Parses a pending-actions JSON blob. Returns an empty list on null/empty/malformed input —
        /// ingest must never fail because of a corrupt queue column.
        /// </summary>
        public static List<ServerAction> DeserializePendingActions(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new List<ServerAction>();

            try
            {
                return JsonConvert.DeserializeObject<List<ServerAction>>(json) ?? new List<ServerAction>();
            }
            catch
            {
                return new List<ServerAction>();
            }
        }
    }
}
