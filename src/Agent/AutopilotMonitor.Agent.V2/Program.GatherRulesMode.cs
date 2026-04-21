using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.DeviceInfo;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2
{
    /// <summary>
    /// <c>--run-gather-rules</c> standalone diagnostic mode. Plan §4.x M4.6.δ.
    /// <para>
    /// Fetches remote config, executes all startup gather rules against a freshly-registered
    /// ephemeral session, uploads the collected events, and exits. Parity with Legacy
    /// <c>Program.GatherRulesMode.cs</c> — the V2 differences are minimal (V2 TenantIdResolver
    /// instead of GetTenantIdFromRegistry, V2 BackendApiClient ctor).
    /// </para>
    /// </summary>
    public static partial class Program
    {
        private const int GatherRulesMaxWaitSeconds = 120;

        internal static int RunGatherRulesMode(string[] args)
        {
            var consoleMode = args.Contains("--console") || Environment.UserInteractive;
            var logDir = Environment.ExpandEnvironmentVariables(Constants.LogDirectory);
            var logger = new AgentLogger(logDir) { EnableConsoleOutput = consoleMode };
            var agentVersion = GetAgentVersion();

            if (consoleMode)
            {
                Console.WriteLine("Autopilot Monitor Agent V2 — Run Gather Rules");
                Console.WriteLine("=============================================");
                Console.WriteLine();
            }

            try
            {
                var tenantId = TenantIdResolver.ResolveFromEnrollmentRegistry(logger);
                if (string.IsNullOrEmpty(tenantId))
                {
                    logger.Error("--run-gather-rules: TenantId could not be resolved — device is not MDM-enrolled.");
                    if (consoleMode) Console.Error.WriteLine("ERROR: device is not MDM-enrolled (no TenantId).");
                    return 2;
                }

                // Always use a fresh ephemeral session id — keeps the gather-rules run out of
                // any active enrollment session's spool / journal.
                var sessionId = Guid.NewGuid().ToString();
                var apiBaseUrl = GetArgValue(args, "--api-url", "--backend-api") ?? Constants.ApiBaseUrl;

                var config = new AgentConfiguration
                {
                    ApiBaseUrl = apiBaseUrl,
                    SessionId = sessionId,
                    TenantId = tenantId,
                    LogDirectory = logDir,
                    SpoolDirectory = Environment.ExpandEnvironmentVariables(Constants.SpoolDirectory),
                    UseClientCertAuth = true,
                    ImeLogPathOverride = GetArgValue(args, "--ime-log-path"),
                    CommandLineArgs = FormatArgsForGatherLog(args),
                };

                logger.Info("======================= --run-gather-rules mode =======================");

                if (consoleMode)
                {
                    Console.WriteLine($"Session ID: {sessionId}  (new, ephemeral)");
                    Console.WriteLine($"Tenant ID:  {tenantId}");
                    Console.WriteLine($"API URL:    {apiBaseUrl}");
                    Console.WriteLine();
                }

                using (var apiClient = new BackendApiClient(apiBaseUrl, config, logger, agentVersion))
                using (var remoteConfigService = new RemoteConfigService(apiClient, tenantId, logger))
                {
                    // Step 1 — fetch remote config.
                    AgentConfigResponse remoteConfig = null;
                    try
                    {
                        var configTask = remoteConfigService.FetchConfigAsync();
                        configTask.Wait(TimeSpan.FromSeconds(15));
                        remoteConfig = remoteConfigService.CurrentConfig;
                        logger.Info("Remote config fetched.");
                        if (consoleMode) Console.WriteLine("Remote config fetched.");
                    }
                    catch (Exception ex)
                    {
                        logger.Warning($"Remote config fetch failed (continuing): {ex.Message}");
                        if (consoleMode) Console.WriteLine($"WARNING: Remote config fetch failed: {ex.Message}");
                    }

                    var rules = remoteConfig?.GatherRules;
                    var startupRules = rules?
                        .Where(r => r.Enabled && r.Trigger == "startup")
                        .ToList() ?? new List<GatherRule>();

                    if (startupRules.Count == 0)
                    {
                        logger.Info("No enabled startup gather rules found — nothing to execute.");
                        if (consoleMode) Console.WriteLine("No startup gather rules found. Nothing to execute.");
                        return 0;
                    }

                    // Step 2 — register the ephemeral session.
                    try
                    {
                        var hw = HardwareInfo.GetHardwareInfo(logger);
                        var registration = new SessionRegistration
                        {
                            SessionId = sessionId,
                            TenantId = tenantId,
                            SerialNumber = hw.SerialNumber,
                            Manufacturer = hw.Manufacturer,
                            Model = hw.Model,
                            DeviceName = Environment.MachineName,
                            OsName = DeviceInfoProvider.GetOsName(),
                            OsBuild = DeviceInfoProvider.GetOsBuild(),
                            OsDisplayVersion = DeviceInfoProvider.GetOsDisplayVersion(),
                            OsEdition = DeviceInfoProvider.GetOsEdition(),
                            OsLanguage = System.Globalization.CultureInfo.CurrentCulture.Name,
                            StartedAt = DateTime.UtcNow,
                            AgentVersion = agentVersion,
                            EnrollmentType = "gather_rules",
                        };

                        var regResponse = apiClient.RegisterSessionAsync(registration).GetAwaiter().GetResult();
                        if (regResponse != null && regResponse.Success)
                            logger.Info($"Session registered: {regResponse.SessionId}");
                        else
                            logger.Warning($"Session registration: {regResponse?.Message ?? "null response"}");
                    }
                    catch (Exception ex)
                    {
                        logger.Warning($"Session registration failed (continuing): {ex.Message}");
                    }

                    // Step 3 — collect events in-memory, never touch the live agent's spool.
                    var collectedEvents = new List<EnrollmentEvent>();
                    var evtLock = new object();
                    long sequence = 0;

                    Action<EnrollmentEvent> emitEvent = evt =>
                    {
                        evt.Sequence = Interlocked.Increment(ref sequence);
                        lock (evtLock) collectedEvents.Add(evt);
                        logger.Debug($"Gather event: {evt.EventType} — {evt.Message}");
                        if (consoleMode)
                            Console.WriteLine($"  [{evt.Timestamp:HH:mm:ss}] [{evt.EventType}] {evt.Message}");
                    };

                    emitEvent(new EnrollmentEvent
                    {
                        SessionId = sessionId,
                        TenantId = tenantId,
                        Timestamp = DateTime.UtcNow,
                        EventType = "gather_rules_collection_started",
                        Severity = EventSeverity.Info,
                        Source = "Agent",
                        Phase = EnrollmentPhase.Start,
                        Message = $"Gather rules collection started ({startupRules.Count} rule(s))",
                        Data = new Dictionary<string, object>
                        {
                            { "ruleCount", startupRules.Count },
                            { "agentVersion", agentVersion },
                        },
                    });

                    // Step 4 — run the executor and wait.
                    using (var executor = new GatherRuleExecutor(sessionId, tenantId, emitEvent, logger, config.ImeLogPathOverride))
                    {
                        executor.UpdateRules(rules);

                        if (consoleMode) Console.WriteLine($"Running {startupRules.Count} rule(s)...");
                        var allCompleted = executor.WaitForStartupRules(GatherRulesMaxWaitSeconds);
                        if (!allCompleted)
                        {
                            logger.Warning($"Some gather rules did not complete within {GatherRulesMaxWaitSeconds}s.");
                            if (consoleMode) Console.WriteLine($"WARNING: Some rules timed out after {GatherRulesMaxWaitSeconds}s.");
                        }
                        else
                        {
                            logger.Info("All startup rules completed.");
                            if (consoleMode) Console.WriteLine("All rules completed.");
                        }
                    }

                    // Step 5 — emit completion event, then upload.
                    List<EnrollmentEvent> eventsToUpload;
                    lock (evtLock) eventsToUpload = new List<EnrollmentEvent>(collectedEvents);

                    var totalEventCount = eventsToUpload.Count + 1;
                    emitEvent(new EnrollmentEvent
                    {
                        SessionId = sessionId,
                        TenantId = tenantId,
                        Timestamp = DateTime.UtcNow,
                        EventType = "gather_rules_collection_completed",
                        Severity = EventSeverity.Info,
                        Source = "Agent",
                        Phase = EnrollmentPhase.Start,
                        Message = $"Gather rules collection completed ({totalEventCount} event(s) collected)",
                        Data = new Dictionary<string, object> { { "totalEvents", totalEventCount } },
                    });
                    lock (evtLock) eventsToUpload = new List<EnrollmentEvent>(collectedEvents);

                    // Step 6 — upload.
                    if (eventsToUpload.Count > 0)
                    {
                        try
                        {
                            var request = new IngestEventsRequest
                            {
                                SessionId = sessionId,
                                TenantId = tenantId,
                                Events = eventsToUpload,
                            };
                            var uploadResponse = apiClient.IngestEventsAsync(request).GetAwaiter().GetResult();
                            if (uploadResponse != null && uploadResponse.Success)
                            {
                                logger.Info($"Uploaded {uploadResponse.EventsProcessed} event(s) successfully.");
                                if (consoleMode) Console.WriteLine($"Uploaded {uploadResponse.EventsProcessed} event(s) successfully.");
                            }
                            else
                            {
                                logger.Warning($"Upload failed: {uploadResponse?.Message ?? "null response"}");
                                if (consoleMode) Console.WriteLine($"Upload failed: {uploadResponse?.Message ?? "null response"}");
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.Error("Failed to upload events.", ex);
                            if (consoleMode) Console.Error.WriteLine($"ERROR uploading events: {ex.Message}");
                        }
                    }
                    else
                    {
                        logger.Info("No events were collected.");
                        if (consoleMode) Console.WriteLine("No events were collected.");
                    }
                }

                if (consoleMode) Console.WriteLine("\nGather rules run complete.");
                return 0;
            }
            catch (Exception ex)
            {
                logger.Error("--run-gather-rules failed.", ex);
                if (consoleMode) Console.Error.WriteLine($"ERROR: {ex.Message}");
                return 1;
            }
        }

        private static string FormatArgsForGatherLog(string[] args) => FormatArgsForLog(args);
    }
}
