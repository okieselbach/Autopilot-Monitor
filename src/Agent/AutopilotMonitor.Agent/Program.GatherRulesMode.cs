using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Agent.Core.Configuration;
using AutopilotMonitor.Agent.Core.Monitoring.Collectors;
using AutopilotMonitor.Agent.Core.Monitoring.Core;
using AutopilotMonitor.Agent.Core.Monitoring.Network;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent
{
    partial class Program
    {
        /// <summary>
        /// Fetches the latest remote config, executes all startup gather rules against the
        /// current session (or a freshly registered one if no session.id exists), uploads the
        /// collected events, then exits. Intended for manual testing and debugging.
        /// </summary>
        static void RunGatherRulesMode(string[] args)
        {
            var consoleMode = args.Contains("--console") || Environment.UserInteractive;

            if (consoleMode)
            {
                Console.WriteLine("Autopilot Monitor Agent — Run Gather Rules");
                Console.WriteLine("==========================================");
                Console.WriteLine();
            }

            try
            {
                var config = LoadConfiguration(args);
                var logDir = Environment.ExpandEnvironmentVariables(config.LogDirectory);
                var logger = new AgentLogger(logDir, config.LogLevel);
                var agentVersion = GetAgentVersion();

                // Always use a fresh session so gather-rules runs appear as a
                // distinct portal entry and never pollute an active enrollment session.
                config.SessionId = Guid.NewGuid().ToString();

                logger.Info("======================= --run-gather-rules mode =======================");

                if (consoleMode)
                {
                    Console.WriteLine($"Session ID: {config.SessionId}  (new, ephemeral)");
                    Console.WriteLine($"Tenant ID:  {config.TenantId}");
                    Console.WriteLine($"API URL:    {config.ApiBaseUrl}");
                    Console.WriteLine();
                }

                using (var apiClient = new BackendApiClient(config.ApiBaseUrl, config, logger, agentVersion))
                using (var remoteConfigService = new RemoteConfigService(apiClient, config.TenantId, logger))
                {
                    // Step 1: Fetch remote config (gather rules + settings)
                    logger.Info("Fetching remote configuration...");
                    if (consoleMode) Console.WriteLine("Fetching remote configuration...");

                    try
                    {
                        remoteConfigService.FetchConfigAsync().Wait(TimeSpan.FromSeconds(15));
                        logger.Info("Remote config fetched successfully");
                        if (consoleMode) Console.WriteLine("Remote config fetched.");
                    }
                    catch (Exception ex)
                    {
                        logger.Warning($"Failed to fetch remote config (continuing): {ex.Message}");
                        if (consoleMode) Console.WriteLine($"WARNING: Remote config fetch failed: {ex.Message}");
                    }

                    var remoteConfig = remoteConfigService.CurrentConfig;
                    var rules = remoteConfig?.GatherRules;

                    var startupRules = rules?
                        .Where(r => r.Enabled && r.Trigger == "startup")
                        .ToList()
                        ?? new List<GatherRule>();

                    if (startupRules.Count == 0)
                    {
                        logger.Info("No enabled startup gather rules found — nothing to execute");
                        if (consoleMode) Console.WriteLine("No startup gather rules found. Nothing to execute.");
                        return;
                    }

                    // Step 2: Register session (or re-use existing — backend handles both)
                    logger.Info("Registering session with backend...");
                    if (consoleMode) Console.WriteLine("Registering session...");

                    try
                    {
                        var registration = new SessionRegistration
                        {
                            SessionId = config.SessionId,
                            TenantId = config.TenantId,
                            SerialNumber = DeviceInfoProvider.GetSerialNumber(),
                            Manufacturer = DeviceInfoProvider.GetManufacturer(),
                            Model = DeviceInfoProvider.GetModel(),
                            DeviceName = Environment.MachineName,
                            OsBuild = Environment.OSVersion.Version.ToString(),
                            OsEdition = DeviceInfoProvider.GetOsEdition(),
                            OsLanguage = System.Globalization.CultureInfo.CurrentCulture.Name,
                            StartedAt = DateTime.UtcNow,
                            AgentVersion = agentVersion,
                            EnrollmentType = "gather_rules"
                        };

                        var regResponse = apiClient.RegisterSessionAsync(registration).Result;
                        if (regResponse.Success)
                            logger.Info($"Session registered: {regResponse.SessionId}");
                        else
                            logger.Warning($"Session registration: {regResponse.Message}");
                    }
                    catch (Exception ex)
                    {
                        logger.Warning($"Session registration failed (continuing): {ex.Message}");
                    }

                    // Step 3: Collect events in-memory (avoids conflicting with a running agent's spool)
                    var collectedEvents = new List<EnrollmentEvent>();
                    var evtLock = new object();
                    long sequence = 0;

                    Action<EnrollmentEvent> emitEvent = evt =>
                    {
                        evt.Sequence = Interlocked.Increment(ref sequence);
                        lock (evtLock)
                            collectedEvents.Add(evt);
                        logger.Debug($"Gather event: {evt.EventType} — {evt.Message}");
                        if (consoleMode)
                            Console.WriteLine($"  [{evt.Timestamp:HH:mm:ss}] [{evt.EventType}] {evt.Message}");
                    };

                    // Step 4: Execute startup gather rules
                    logger.Info($"Executing {startupRules.Count} startup gather rule(s)...");
                    if (consoleMode)
                        Console.WriteLine($"Executing {startupRules.Count} startup gather rule(s)...");

                    emitEvent(new EnrollmentEvent
                    {
                        SessionId = config.SessionId,
                        TenantId = config.TenantId,
                        Timestamp = DateTime.UtcNow,
                        EventType = "gather_rules_collection_started",
                        Severity = EventSeverity.Info,
                        Source = "Agent",
                        Phase = EnrollmentPhase.Start,
                        Message = $"Gather rules collection started ({startupRules.Count} rule(s))",
                        Data = new Dictionary<string, object>
                        {
                            { "ruleCount", startupRules.Count },
                            { "agentVersion", agentVersion }
                        }
                    });

                    using (var executor = new GatherRuleExecutor(
                        config.SessionId,
                        config.TenantId,
                        emitEvent,
                        logger,
                        config.ImeLogPathOverride))
                    {
                        executor.UpdateRules(rules);

                        // Step 5: Wait for all startup rules to finish — no fixed sleep,
                        // exits as soon as the last rule completes (120 s hard cap).
                        const int maxWaitSeconds = 120;
                        logger.Info($"Waiting for startup rules to complete (max {maxWaitSeconds}s)...");
                        if (consoleMode)
                            Console.WriteLine($"Running {startupRules.Count} rule(s)...");

                        var allCompleted = executor.WaitForStartupRules(maxWaitSeconds);
                        if (!allCompleted)
                        {
                            logger.Warning($"Some gather rules did not complete within {maxWaitSeconds}s (results may be incomplete)");
                            if (consoleMode)
                                Console.WriteLine($"WARNING: Some rules timed out after {maxWaitSeconds}s.");
                        }
                        else
                        {
                            logger.Info("All startup rules completed");
                            if (consoleMode) Console.WriteLine("All rules completed.");
                        }
                    }

                    // Step 6: Upload all collected events
                    List<EnrollmentEvent> eventsToUpload;
                    lock (evtLock)
                        eventsToUpload = new List<EnrollmentEvent>(collectedEvents);

                    // +1 because the completed event itself is also uploaded
                    var totalEventCount = eventsToUpload.Count + 1;
                    emitEvent(new EnrollmentEvent
                    {
                        SessionId = config.SessionId,
                        TenantId = config.TenantId,
                        Timestamp = DateTime.UtcNow,
                        EventType = "gather_rules_collection_completed",
                        Severity = EventSeverity.Info,
                        Source = "Agent",
                        Phase = EnrollmentPhase.Start,
                        Message = $"Gather rules collection completed ({totalEventCount} event(s) collected)",
                        Data = new Dictionary<string, object>
                        {
                            { "totalEvents", totalEventCount }
                        }
                    });
                    // Re-snapshot after adding the completed event
                    lock (evtLock)
                        eventsToUpload = new List<EnrollmentEvent>(collectedEvents);

                    logger.Info($"Uploading {eventsToUpload.Count} collected event(s)...");
                    if (consoleMode)
                        Console.WriteLine($"Uploading {eventsToUpload.Count} event(s)...");

                    if (eventsToUpload.Count > 0)
                    {
                        try
                        {
                            var request = new IngestEventsRequest
                            {
                                SessionId = config.SessionId,
                                TenantId = config.TenantId,
                                Events = eventsToUpload
                            };

                            var uploadResponse = apiClient.IngestEventsAsync(request).Result;
                            if (uploadResponse.Success)
                            {
                                logger.Info($"Uploaded {uploadResponse.EventsProcessed} event(s) successfully");
                                if (consoleMode)
                                    Console.WriteLine($"Uploaded {uploadResponse.EventsProcessed} event(s) successfully.");
                            }
                            else
                            {
                                logger.Warning($"Upload failed: {uploadResponse.Message}");
                                if (consoleMode)
                                    Console.WriteLine($"Upload failed: {uploadResponse.Message}");
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.Error("Failed to upload events", ex);
                            if (consoleMode)
                                Console.WriteLine($"ERROR uploading events: {ex.Message}");
                        }
                    }
                    else
                    {
                        logger.Info("No events were collected");
                        if (consoleMode)
                            Console.WriteLine("No events were collected.");
                    }
                }

                if (consoleMode)
                    Console.WriteLine("\nGather rules run complete.");
            }
            catch (Exception ex)
            {
                if (consoleMode)
                {
                    Console.WriteLine($"ERROR: {ex.Message}");
                    Console.WriteLine(ex.StackTrace);
                }
            }
        }
    }
}
