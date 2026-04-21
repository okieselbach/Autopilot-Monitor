using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Runtime;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Termination;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2
{
    /// <summary>
    /// V2-Agent entry point. Plan §4.x M4.5.b + M4.6.α.
    /// <para>
    /// Boot sequence (same order as Legacy for feature parity):
    /// </para>
    /// <list type="number">
    ///   <item><c>--help</c> / <c>--version</c> short-circuit</item>
    ///   <item><c>--install</c> forks to <see cref="RunInstallMode"/> and exits</item>
    ///   <item>Multi-instance guard — single-agent invariant</item>
    ///   <item>Register <c>ProcessExit</c> → writes <c>clean-exit.marker</c></item>
    ///   <item>Ensure agent directories exist</item>
    ///   <item><see cref="SelfUpdater.LogInit"/> + <see cref="SelfUpdater.CleanupPreviousUpdate"/></item>
    ///   <item>Load cached <c>remote-config.json</c> → <see cref="SelfUpdater.BackendExpectedSha256"/> + <c>AllowAgentDowngrade</c></item>
    ///   <item><see cref="SelfUpdater.CheckAndApplyUpdateAsync"/> — on success restarts the process; on failure continues with current binary</item>
    ///   <item><see cref="DetectPreviousExit"/> — reads markers + event log to classify last shutdown</item>
    ///   <item><see cref="CheckEnrollmentCompleteMarker"/> — ghost-restart detection + cleanup retry</item>
    ///   <item><see cref="CheckSessionAgeEmergencyBreak"/> — absolute session-age watchdog</item>
    ///   <item>Resolve TenantId (registry → bootstrap-config.json fallback)</item>
    ///   <item>Build <see cref="AgentConfiguration"/> (CLI args + persisted bootstrap / await-enrollment config)</item>
    ///   <item>Get/create SessionId via <see cref="SessionIdPersistence"/></item>
    ///   <item>(Optional) Wait for MDM certificate in <c>--await-enrollment</c> mode</item>
    ///   <item>Build <see cref="BackendApiClient"/> + <see cref="RemoteConfigService"/> → fetch config</item>
    ///   <item><see cref="BootstrapConfigCleanup.TryDeleteIfCertReadyAsync"/> — H-2 mitigation post-cert</item>
    ///   <item>Build mTLS <see cref="HttpClient"/> → <see cref="BackendTelemetryUploader"/></item>
    ///   <item>Build <see cref="DefaultComponentFactory"/> + <see cref="EnrollmentOrchestrator"/></item>
    ///   <item><c>orchestrator.Start()</c> → emit <see cref="VersionCheckEventBuilder"/>-derived event</item>
    ///   <item>Wait for Ctrl+C / ProcessExit / EnrollmentTerminated</item>
    ///   <item><c>orchestrator.Stop()</c>, exit 0</item>
    /// </list>
    /// </summary>
    public static partial class Program
    {
        private const string DefaultStateDirectory = @"%ProgramData%\AutopilotMonitor";
        private const string DefaultLogDirectory = @"%ProgramData%\AutopilotMonitor\Logs";
        private const string DefaultAgentSubdirectory = "Agent";
        private const string DefaultV2StateSubdirectory = "V2";
        private const string DefaultTransportSubdirectory = "V2Transport";
        private const string CachedRemoteConfigPath = @"%ProgramData%\AutopilotMonitor\Config\remote-config.json";

        public static int Main(string[] args)
        {
            if (args.Contains("--help") || args.Contains("-h") || args.Contains("-?"))
            {
                PrintUsage();
                return 0;
            }

            if (args.Contains("--version"))
            {
                PrintVersion();
                return 0;
            }

            // --install forks to a separate flow that exits when done (never falls through into RunAgent).
            if (args.Contains("--install"))
            {
                return RunInstallMode(args);
            }

            // --run-gather-rules / --run-ime-matching: standalone diagnostic modes (M4.6.δ).
            // They neither touch the Scheduled Task nor the live agent's spool — safe to run
            // alongside a normal monitoring agent for troubleshooting.
            if (args.Contains("--run-gather-rules"))
            {
                return RunGatherRulesMode(args);
            }

            if (args.Contains("--run-ime-matching"))
            {
                return RunImeMatchingMode(args);
            }

            var consoleMode = args.Contains("--console") || Environment.UserInteractive;

            // Multi-instance guard — prevents a second agent process from running alongside one
            // that was started by the Scheduled Task.
            if (IsAnotherAgentInstanceRunning())
            {
                var msg = "Another agent process is already running. This instance will exit.";
                if (consoleMode) Console.Error.WriteLine($"ERROR: {msg}");
                try
                {
                    var earlyLogger = new AgentLogger(Environment.ExpandEnvironmentVariables(DefaultLogDirectory));
                    earlyLogger.Warning(msg);
                }
                catch { /* best-effort */ }
                return 1;
            }

            var dataDirectory = Environment.ExpandEnvironmentVariables(DefaultStateDirectory);
            var logDirectory = Environment.ExpandEnvironmentVariables(DefaultLogDirectory);

            try { Directory.CreateDirectory(dataDirectory); } catch { }
            try { Directory.CreateDirectory(logDirectory); } catch { }

            // Register the clean-exit marker writer BEFORE any risky startup work so an OS
            // shutdown during self-update still produces a "clean" classification.
            RegisterCleanExitMarker(dataDirectory);

            // Startup self-update. Legacy parity: cleanup leftover .old files, load cached
            // backend hash / downgrade policy, then attempt the update. Failures here never
            // abort startup — we prefer to run the current version than delay.
            SelfUpdater.LogInit(GetAgentVersion());

            var agentDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            SelfUpdater.CleanupPreviousUpdate(agentDir, msg => { if (consoleMode) Console.Out.WriteLine(msg); });

            var allowAgentDowngrade = LoadCachedSelfUpdateContext();

            try
            {
                SelfUpdater.CheckAndApplyUpdateAsync(
                    currentVersion: GetAgentVersion(),
                    agentDir: agentDir,
                    consoleMode: consoleMode,
                    allowDowngrade: allowAgentDowngrade).GetAwaiter().GetResult();
            }
            catch (Exception selfUpdateEx)
            {
                // SelfUpdater is designed to swallow its own errors; catch anything that still
                // escapes so startup does not abort on an unexpected failure in the update path.
                SelfUpdater.Log($"Self-update outer exception: {selfUpdateEx.Message}");
            }

            // At this point either (a) no update was applied, or (b) update applied but restart
            // didn't happen — continue startup with the current binary.

            var logger = new AgentLogger(logDirectory) { EnableConsoleOutput = consoleMode };
            logger.Info($"AutopilotMonitor.Agent.V2 starting (version {GetAgentVersion()}).");
            logger.Info($"Command line: {FormatArgsForLog(args)}");

            try
            {
                return RunAgent(args, logger, dataDirectory, logDirectory, consoleMode);
            }
            catch (Exception ex)
            {
                logger.Error("V2 agent startup failed.", ex);
                WriteCrashLog(logDirectory, ex);
                if (consoleMode) Console.Error.WriteLine($"FATAL: {ex.Message}");
                return 1;
            }
        }

        // ---------------------------------------------------------------- Orchestration

        private static int RunAgent(
            string[] args,
            AgentLogger logger,
            string dataDirectory,
            string logDirectory,
            bool consoleMode)
        {
            var stateSubdir = Path.Combine(dataDirectory, DefaultV2StateSubdirectory);
            var transportDir = Path.Combine(dataDirectory, DefaultTransportSubdirectory);

            // Previous-exit classification (for the agent_started event + observability).
            var previousExit = DetectPreviousExit(dataDirectory, logDirectory);
            if (previousExit.ExitType != "first_run")
            {
                var crashSuffix = previousExit.CrashExceptionType != null ? $" ({previousExit.CrashExceptionType})" : "";
                logger.Info($"Previous exit: {previousExit.ExitType}{crashSuffix}");
            }

            // Merge persisted bootstrap-config.json + await-enrollment.json into CLI args early.
            var bootstrapConfig = TryReadBootstrapConfig(dataDirectory, logger);
            var awaitConfig = TryReadAwaitEnrollmentConfig(dataDirectory, logger);

            var tenantIdFromRegistry = TenantIdResolver.ResolveFromEnrollmentRegistry(logger);
            var tenantId = !string.IsNullOrEmpty(tenantIdFromRegistry)
                ? tenantIdFromRegistry
                : bootstrapConfig?.TenantId;

            // --install time may have written a bootstrap config that holds a tenantId even when
            // the registry is not yet populated (bootstrap token path). Honour that.

            if (args.Contains("--new-session"))
            {
                new SessionIdPersistence(dataDirectory).Delete(logger);
                logger.Info("--new-session: cleared persisted SessionId.");
            }

            var agentConfig = BuildAgentConfiguration(args, tenantId, sessionId: null, bootstrapConfig, awaitConfig);

            // Build a cleanup-service factory used by guards: instantiated lazily and with the
            // current agentConfig so command-line overrides (e.g. --no-cleanup) take effect.
            Func<CleanupService> cleanupServiceFactory = () => new CleanupService(agentConfig, logger);

            if (CheckEnrollmentCompleteMarker(
                    dataDirectory, stateSubdir,
                    agentConfig.SelfDestructOnComplete, cleanupServiceFactory, logger, consoleMode))
            {
                logger.Info("Enrollment-complete marker handled — agent exiting.");
                return 0;
            }

            if (CheckSessionAgeEmergencyBreak(
                    dataDirectory, stateSubdir,
                    agentConfig.AbsoluteMaxSessionHours, agentConfig.SelfDestructOnComplete,
                    cleanupServiceFactory, logger, consoleMode))
            {
                logger.Info("Emergency break fired — agent exiting.");
                return 0;
            }

            // Session is safe to start — create/recover SessionId now (AFTER guards so a dead
            // session is not resurrected before the watchdog can kill it).
            agentConfig.SessionId = new SessionIdPersistence(dataDirectory).GetOrCreate(logger);

            if (agentConfig.AwaitEnrollment)
            {
                logger.Info($"--await-enrollment: polling for MDM certificate (timeout: {agentConfig.AwaitEnrollmentTimeoutMinutes}min).");
                using (var cts = new CancellationTokenSource())
                {
                    var cert = EnrollmentAwaiter
                        .WaitForMdmCertificateAsync(
                            thumbprint: null,
                            timeoutMinutes: agentConfig.AwaitEnrollmentTimeoutMinutes,
                            logger: logger,
                            cancellationToken: cts.Token)
                        .GetAwaiter().GetResult();
                    if (cert == null)
                    {
                        logger.Error("Await-enrollment: timed out waiting for MDM certificate — exiting.");
                        return 3;
                    }
                }

                // Re-resolve TenantId — enrollment typically writes the registry key alongside the cert.
                if (string.IsNullOrEmpty(agentConfig.TenantId))
                {
                    agentConfig.TenantId = TenantIdResolver.ResolveFromEnrollmentRegistry(logger);
                    if (!string.IsNullOrEmpty(agentConfig.TenantId))
                        logger.Info($"Await-enrollment: TenantId discovered from registry: {agentConfig.TenantId}");
                }

                // Await-enrollment is one-shot — remove the persisted config so subsequent restarts proceed normally.
                DeleteAwaitEnrollmentConfig(dataDirectory, logger);
            }

            if (string.IsNullOrEmpty(agentConfig.TenantId))
            {
                logger.Error("V2 agent cannot start: TenantId not available (registry empty + no bootstrap config).");
                return 2;
            }

            var backendApiClient = new BackendApiClient(
                baseUrl: agentConfig.ApiBaseUrl,
                configuration: agentConfig,
                logger: logger,
                agentVersion: GetAgentVersion());

            // M4.6.γ — Emergency + Distress reporters. Plumbed into RemoteConfigService so
            // Config-fetch failures (auth vs network) flow to the correct channel. Also fires
            // an initial AuthCertificateMissing distress when the MDM cert was expected but
            // not found (Legacy parity — surfaces pre-MDM-enrollment dead-ends to the backend
            // via the cert-less distress channel).
            var hardwareForReporters = HardwareInfo.GetHardwareInfo(logger);
            var distressReporter = new DistressReporter(
                baseUrl: agentConfig.ApiBaseUrl,
                tenantId: agentConfig.TenantId,
                manufacturer: hardwareForReporters.Manufacturer,
                model: hardwareForReporters.Model,
                serialNumber: hardwareForReporters.SerialNumber,
                agentVersion: GetAgentVersion(),
                logger: logger);

            var emergencyReporter = new EmergencyReporter(
                apiClient: backendApiClient,
                sessionId: agentConfig.SessionId,
                tenantId: agentConfig.TenantId,
                agentVersion: GetAgentVersion(),
                logger: logger);

            if (agentConfig.UseClientCertAuth && backendApiClient.ClientCertificate == null)
            {
                _ = distressReporter.TrySendAsync(
                    DistressErrorType.AuthCertificateMissing,
                    "MDM certificate not found in LocalMachine or CurrentUser store");
            }

            var remoteConfigService = new RemoteConfigService(
                backendApiClient, agentConfig.TenantId, logger, emergencyReporter, distressReporter);
            var remoteConfig = remoteConfigService.FetchConfigAsync().GetAwaiter().GetResult();

            logger.SetLogLevel(agentConfig.LogLevel);

            // Propagate the backend-expected SHA so the runtime hash-mismatch trigger (M4.6.α
            // continues this wire; actual runtime trigger will be wired via ServerActionDispatcher
            // in M4.6.β) has the up-to-date integrity hash. Also refresh AllowAgentDowngrade.
            if (!string.IsNullOrEmpty(remoteConfig.LatestAgentSha256))
                SelfUpdater.BackendExpectedSha256 = remoteConfig.LatestAgentSha256;

            // H-2 mitigation: delete the persisted bootstrap-config.json once the MDM cert
            // proves it can authenticate. Non-blocking — any failure leaves the file for retry.
            try
            {
                BootstrapConfigCleanup
                    .TryDeleteIfCertReadyAsync(agentConfig, logger, GetAgentVersion())
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.Debug($"BootstrapConfigCleanup outer exception: {ex.Message}");
            }

            HttpClient mtlsHttpClient;
            BackendTelemetryUploader uploader;
            try
            {
                mtlsHttpClient = MtlsHttpClientFactory.Create(
                    resolver: new DefaultCertificateResolver(),
                    logger: logger);
            }
            catch (InvalidOperationException ex)
            {
                logger.Error("mTLS HttpClient creation failed — cannot upload telemetry.", ex);
                return 4;
            }

            try
            {
                uploader = new BackendTelemetryUploader(
                    httpClient: mtlsHttpClient,
                    baseUrl: agentConfig.ApiBaseUrl,
                    tenantId: agentConfig.TenantId,
                    manufacturer: hardwareForReporters.Manufacturer,
                    model: hardwareForReporters.Model,
                    serialNumber: hardwareForReporters.SerialNumber,
                    bootstrapToken: agentConfig.UseBootstrapTokenAuth ? agentConfig.BootstrapToken : null,
                    agentVersion: GetAgentVersion());
            }
            catch (Exception ex)
            {
                logger.Error("BackendTelemetryUploader construction failed.", ex);
                return 5;
            }

            var classifiers = new IClassifier[]
            {
                new WhiteGloveSealingClassifier(),
                new WhiteGlovePart2CompletionClassifier(),
            };

            var componentFactory = new DefaultComponentFactory(
                agentConfig: agentConfig,
                remoteConfig: remoteConfig,
                networkMetrics: backendApiClient.NetworkMetrics,
                agentVersion: GetAgentVersion(),
                stateDirectory: stateSubdir);

            var whiteGloveSealingPatternIds = (System.Collections.Generic.IReadOnlyCollection<string>)remoteConfig.WhiteGloveSealingPatternIds
                ?? Array.Empty<string>();

            var agentMaxLifetime = agentConfig.AgentMaxLifetimeMinutes > 0
                ? (TimeSpan?)TimeSpan.FromMinutes(agentConfig.AgentMaxLifetimeMinutes)
                : null;

            // Diagnostics upload delegate — wraps the production DiagnosticsPackageService.
            // Instantiated lazily + per-invocation (cheap) so we always pick up current config.
            var diagnosticsService = new DiagnosticsPackageService(agentConfig, logger, backendApiClient);
            Func<bool, string, Task<DiagnosticsUploadResult>> uploadDiagnosticsAsync =
                (succeeded, suffix) => diagnosticsService.CreateAndUploadAsync(succeeded, suffix);

            var agentStartTimeUtc = DateTime.UtcNow;

            using (var orchestrator = new EnrollmentOrchestrator(
                sessionId: agentConfig.SessionId,
                tenantId: agentConfig.TenantId,
                stateDirectory: stateSubdir,
                transportDirectory: transportDir,
                clock: SystemClock.Instance,
                logger: logger,
                uploader: uploader,
                classifiers: classifiers,
                componentFactory: componentFactory,
                whiteGloveSealingPatternIds: whiteGloveSealingPatternIds,
                agentMaxLifetime: agentMaxLifetime))
            {
                using (var shutdown = new ManualResetEventSlim(false))
                {
                    // M4.6.δ — Analyzer manager. Emits through the live orchestrator's event
                    // emitter. RunStartup fires after orchestrator.Start; RunShutdown is wired
                    // into the termination handler so it runs before diagnostics upload.
                    var analyzerManager = new AgentAnalyzerManager(
                        configuration: agentConfig,
                        logger: logger,
                        emitEvent: evt => { orchestrator.EventEmitter.Emit(evt); },
                        analyzerConfig: remoteConfig.Analyzers);

                    // M4.6.β — the peripheral termination sequence (FinalStatus + SummaryDialog
                    // + diagnostics upload + enrollment-complete.marker + CleanupService) lives
                    // in Program.cs, not in the kernel. We compose it here and hook it onto the
                    // orchestrator's typed Terminated event.
                    var terminationHandler = new EnrollmentTerminationHandler(
                        configuration: agentConfig,
                        logger: logger,
                        stateDirectory: stateSubdir,
                        agentStartTimeUtc: agentStartTimeUtc,
                        currentStateAccessor: () => orchestrator.CurrentState,
                        packageStatesAccessor: () => componentFactory.ImePackageStates,
                        cleanupServiceFactory: () => new CleanupService(agentConfig, logger),
                        uploadDiagnosticsAsync: uploadDiagnosticsAsync,
                        signalShutdown: () => shutdown.Set(),
                        analyzerManager: analyzerManager);

                    // ServerActionDispatcher — handlers are live and the M4.6.ε response-parse
                    // path below feeds it. RotateConfig refetches remote config;
                    // RequestDiagnostics triggers the same diagnostics pipeline as enrollment-end;
                    // TerminateSession routes to the termination handler (reason: server-requested).
                    var serverActionDispatcher = new ServerActionDispatcher(
                        configuration: agentConfig,
                        logger: logger,
                        rotateConfigAsync: async () =>
                        {
                            try { var _ = await remoteConfigService.FetchConfigAsync(); return true; }
                            catch (Exception ex) { logger.Warning($"ServerAction rotate_config failed: {ex.Message}"); return false; }
                        },
                        uploadDiagnosticsAsync: async (suffix) =>
                            await diagnosticsService.CreateAndUploadAsync(enrollmentSucceeded: false, fileNameSuffix: suffix),
                        onTerminateRequested: action =>
                        {
                            var forceSelfDestruct = action?.Params != null
                                && action.Params.TryGetValue("forceSelfDestruct", out var f)
                                && string.Equals(f, "true", StringComparison.OrdinalIgnoreCase);
                            if (forceSelfDestruct && !agentConfig.SelfDestructOnComplete)
                            {
                                logger.Warning("ServerAction terminate_session: forceSelfDestruct=true overrides SelfDestructOnComplete=false.");
                                agentConfig.SelfDestructOnComplete = true;
                            }

                            // Codex Finding 2: forward adminOutcome from the ServerAction params so
                            // a portal Mark-Succeeded really lands as Succeeded locally (was
                            // hard-coded to Failed before, masquerading every admin override as an
                            // error in SummaryDialog + firing spurious diagnostics uploads).
                            var mappedOutcome = MapAdminOutcome(action?.Params);

                            logger.Warning($"ServerAction terminate_session received (ruleId={action?.RuleId}, reason={action?.Reason}, outcome={mappedOutcome}) — invoking termination handler.");
                            // Synthesise a Terminated event as if the kernel fired it.
                            terminationHandler.Handle(
                                sender: null,
                                args: new EnrollmentTerminatedEventArgs(
                                    reason: EnrollmentTerminationReason.DecisionTerminalStage,
                                    outcome: mappedOutcome,
                                    stageName: orchestrator.CurrentState?.Stage.ToString(),
                                    terminatedAtUtc: DateTime.UtcNow,
                                    details: $"Server-requested termination: ruleId={action?.RuleId}, reason={action?.Reason}"));
                            return Task.CompletedTask;
                        },
                        emitEvent: evt => { orchestrator.EventEmitter.Emit(evt); });

                    // M4.6.ε — BackendTelemetryUploader response-plumbing. The orchestrator parses
                    // DeviceBlocked / DeviceKillSignal / AdminAction / Actions out of the 2xx
                    // response body (see BackendTelemetryUploader.TryReadControlSignalsAsync) and
                    // raises ServerResponseReceived. We translate those into ServerActions and
                    // dispatch. Legacy parity with IngestEventsResponse synthesis in
                    // EventUploadOrchestrator.OnEventsUploaded().
                    WireTelemetryServerResponse(orchestrator, serverActionDispatcher, logger);

                    ConsoleCancelEventHandler cancelHandler = (s, e) =>
                    {
                        e.Cancel = true;
                        logger.Info("Ctrl+C received — initiating graceful shutdown.");
                        shutdown.Set();
                    };
                    EventHandler processExitHandler = (s, e) =>
                    {
                        logger.Info("ProcessExit — initiating graceful shutdown.");
                        shutdown.Set();
                    };

                    Console.CancelKeyPress += cancelHandler;
                    AppDomain.CurrentDomain.ProcessExit += processExitHandler;
                    orchestrator.Terminated += terminationHandler.Handle;

                    try
                    {
                        orchestrator.Start();

                        // Emit the agent_version_check event now that the EventEmitter is alive.
                        // VersionCheckEventBuilder.TryBuild is a no-op when no markers are present.
                        EmitVersionCheckEventIfAny(orchestrator, agentConfig, logger);

                        // M4.6.δ — fire-and-forget startup analyzers (LocalAdmin / SoftwareInventory /
                        // IntegrityBypass). Runs on a background task inside AgentAnalyzerManager.
                        try { analyzerManager.RunStartup(); }
                        catch (Exception ex) { logger.Warning($"AnalyzerManager.RunStartup threw: {ex.Message}"); }

                        // M4.6.γ — fire-and-forget startup probes (geo / timezone / NTP). Runs on
                        // the ThreadPool so a slow network never delays the critical path.
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await StartupEnvironmentProbes
                                    .RunAsync(agentConfig, logger, orchestrator.EventEmitter)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                logger.Warning($"StartupEnvironmentProbes: outer exception: {ex.Message}");
                            }
                        });

                        logger.Info($"V2 agent runtime ready (session={agentConfig.SessionId}, tenant={agentConfig.TenantId}).");
                        if (consoleMode)
                            Console.Out.WriteLine("AutopilotMonitor.Agent.V2 running. Press Ctrl+C to stop.");

                        shutdown.Wait();
                    }
                    finally
                    {
                        Console.CancelKeyPress -= cancelHandler;
                        AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
                        orchestrator.Terminated -= terminationHandler.Handle;

                        try { orchestrator.Stop(); }
                        catch (Exception ex) { logger.Error("Orchestrator stop failed.", ex); }

                        try { mtlsHttpClient.Dispose(); } catch { }
                        try { backendApiClient.Dispose(); } catch { }
                    }
                }
            }

            logger.Info("AutopilotMonitor.Agent.V2 stopped cleanly.");
            return 0;
        }

        /// <summary>
        /// M4.6.ε — routes <see cref="TelemetryUploadOrchestrator.ServerResponseReceived"/>
        /// control signals through the <see cref="ServerActionDispatcher"/>. Synthesises the
        /// same <c>terminate_session</c> <see cref="ServerAction"/>s that Legacy built in
        /// <c>EventUploadOrchestrator.OnEventsUploaded</c> (kill-signal → force self-destruct;
        /// admin-action → soft terminate respecting <c>SelfDestructOnComplete</c>).
        /// </summary>
        private static void WireTelemetryServerResponse(
            EnrollmentOrchestrator orchestrator,
            ServerActionDispatcher dispatcher,
            AgentLogger logger)
        {
            orchestrator.Transport.ServerResponseReceived += (sender, upload) =>
            {
                var toDispatch = new System.Collections.Generic.List<ServerAction>();

                if (upload.DeviceKillSignal)
                {
                    logger.Warning("Backend signalled DeviceKillSignal — synthesising terminate_session (force self-destruct).");
                    toDispatch.Add(new ServerAction
                    {
                        Type = ServerActionTypes.TerminateSession,
                        Reason = "DeviceKillSignal from administrator",
                        QueuedAt = DateTime.UtcNow,
                        Params = new System.Collections.Generic.Dictionary<string, string>
                        {
                            { "forceSelfDestruct", "true" },
                            { "gracePeriodSeconds", "0" },
                            { "origin", "kill_signal" },
                        },
                    });
                }
                else if (!string.IsNullOrEmpty(upload.AdminAction))
                {
                    logger.Warning($"Backend signalled AdminAction={upload.AdminAction} — synthesising terminate_session (soft).");
                    toDispatch.Add(new ServerAction
                    {
                        Type = ServerActionTypes.TerminateSession,
                        Reason = $"Admin marked session as {upload.AdminAction}",
                        QueuedAt = DateTime.UtcNow,
                        Params = new System.Collections.Generic.Dictionary<string, string>
                        {
                            { "adminOutcome", upload.AdminAction },
                            { "gracePeriodSeconds", "0" },
                            { "origin", "admin_action" },
                        },
                    });
                }

                if (upload.Actions != null && upload.Actions.Count > 0)
                {
                    foreach (var a in upload.Actions) toDispatch.Add(a);
                }

                if (upload.DeviceBlocked)
                {
                    // DeviceBlocked is non-terminal: the transport already paused its drain
                    // loop in TelemetryUploadOrchestrator.ApplyControlSignals; we just log it
                    // here so it surfaces on the console / agent log.
                    var until = upload.UnblockAt.HasValue ? $"until {upload.UnblockAt.Value:O}" : "indefinitely";
                    logger.Warning($"Backend signalled DeviceBlocked {until} — uploads paused, session remains alive.");
                }

                if (toDispatch.Count == 0) return;

                try { dispatcher.DispatchAsync(toDispatch).GetAwaiter().GetResult(); }
                catch (Exception ex) { logger.Error("ServerActionDispatcher.DispatchAsync threw during server-response wiring.", ex); }
            };
        }

        private static void EmitVersionCheckEventIfAny(
            EnrollmentOrchestrator orchestrator,
            AgentConfiguration agentConfig,
            AgentLogger logger)
        {
            try
            {
                var buildResult = VersionCheckEventBuilder.TryBuild(
                    sessionId: agentConfig.SessionId,
                    tenantId: agentConfig.TenantId,
                    agentStartTimeUtc: DateTime.UtcNow);

                if (!string.IsNullOrEmpty(buildResult?.ParseError))
                    logger.Warning($"VersionCheckEventBuilder parse error: {buildResult.ParseError}");

                if (buildResult?.Event != null)
                {
                    orchestrator.EventEmitter.Emit(buildResult.Event);
                    logger.Info($"agent_version_check emitted (outcome={buildResult.Outcome}).");
                }
                else if (buildResult?.Deduped == true)
                {
                    logger.Debug($"agent_version_check deduped (outcome={buildResult.Outcome}).");
                }
            }
            catch (Exception ex)
            {
                logger.Warning($"VersionCheckEventBuilder emission failed: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------- Configuration

        private static AgentConfiguration BuildAgentConfiguration(
            string[] args,
            string tenantId,
            string sessionId,
            BootstrapConfigFile bootstrapConfig,
            AwaitEnrollmentConfigFile awaitConfig)
        {
            var apiBaseUrl = GetArgValue(args, "--api-url", "--backend-api") ?? Constants.ApiBaseUrl;
            var imeLogPathOverride = GetArgValue(args, "--ime-log-path");
            var imeMatchLogPath = GetArgValue(args, "--ime-match-log");

            var bootstrapToken = GetArgValue(args, "--bootstrap-token")
                ?? bootstrapConfig?.BootstrapToken;

            var awaitEnrollment = args.Contains("--await-enrollment") || awaitConfig != null;
            var rebootOnComplete = args.Contains("--reboot-on-complete");
            var disableGeoLocation = args.Contains("--disable-geolocation");
            var keepLogFile = args.Contains("--keep-logfile");
            var noCleanup = args.Contains("--no-cleanup");

            var awaitTimeoutRaw = GetArgValue(args, "--await-enrollment-timeout");
            var awaitTimeoutMinutes = 480;
            if (!string.IsNullOrEmpty(awaitTimeoutRaw) && int.TryParse(awaitTimeoutRaw, out var parsedTimeout))
                awaitTimeoutMinutes = parsedTimeout;
            else if (awaitConfig != null)
                awaitTimeoutMinutes = awaitConfig.TimeoutMinutes;

            var useBootstrapTokenAuth = !string.IsNullOrEmpty(bootstrapToken);

            var cliLogLevel = GetArgValue(args, "--log-level");
            var logLevel = AgentLogLevel.Info;
            if (!string.IsNullOrEmpty(cliLogLevel) && Enum.TryParse<AgentLogLevel>(cliLogLevel, ignoreCase: true, out var parsedLevel))
                logLevel = parsedLevel;

            return new AgentConfiguration
            {
                ApiBaseUrl = apiBaseUrl,
                SessionId = sessionId,
                TenantId = tenantId,
                SpoolDirectory = Environment.ExpandEnvironmentVariables(Constants.SpoolDirectory),
                LogDirectory = Environment.ExpandEnvironmentVariables(Constants.LogDirectory),
                UploadIntervalSeconds = Constants.DefaultUploadIntervalSeconds,
                MaxBatchSize = Constants.MaxBatchSize,
                LogLevel = logLevel,
                UseClientCertAuth = !useBootstrapTokenAuth,
                BootstrapToken = bootstrapToken,
                UseBootstrapTokenAuth = useBootstrapTokenAuth,
                AwaitEnrollment = awaitEnrollment,
                AwaitEnrollmentTimeoutMinutes = awaitTimeoutMinutes,
                RebootOnComplete = rebootOnComplete,
                EnableGeoLocation = !disableGeoLocation,
                ImeLogPathOverride = imeLogPathOverride,
                ImeMatchLogPath = imeMatchLogPath,
                KeepLogFile = keepLogFile,
                SelfDestructOnComplete = !noCleanup,
                CommandLineArgs = FormatArgsForLog(args),
            };
        }

        private static bool LoadCachedSelfUpdateContext()
        {
            try
            {
                var path = Environment.ExpandEnvironmentVariables(CachedRemoteConfigPath);
                if (!File.Exists(path)) return false;

                var json = File.ReadAllText(path);
                var cached = Newtonsoft.Json.JsonConvert.DeserializeObject<AgentConfigResponse>(json);
                if (cached == null) return false;

                if (!string.IsNullOrEmpty(cached.LatestAgentSha256))
                {
                    SelfUpdater.BackendExpectedSha256 = cached.LatestAgentSha256;
                    SelfUpdater.Log(
                        $"Self-update: loaded backend integrity hash from cached config (sha256={cached.LatestAgentSha256.Substring(0, Math.Min(12, cached.LatestAgentSha256.Length))}...)");
                }

                return cached.AllowAgentDowngrade;
            }
            catch (Exception ex)
            {
                SelfUpdater.Log($"Self-update: cached config read failed: {ex.Message}");
                return false;
            }
        }

        internal static string GetArgValue(string[] args, params string[] names)
        {
            if (args == null || args.Length < 2 || names == null) return null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                foreach (var name in names)
                {
                    if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                        return args[i + 1];
                }
            }
            return null;
        }

        private static string FormatArgsForLog(string[] args)
        {
            if (args == null || args.Length == 0) return string.Empty;

            var parts = new System.Collections.Generic.List<string>(args.Length);
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (string.Equals(a, "--bootstrap-token", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    parts.Add(a);
                    parts.Add("[redacted]");
                    i++;
                    continue;
                }
                parts.Add(a);
            }
            return string.Join(" ", parts);
        }

        // ---------------------------------------------------------------- Info

        private static void PrintUsage()
        {
            var v = GetAgentVersion();
            Console.Out.WriteLine($"Autopilot Monitor Agent V2 v{v}");
            Console.Out.WriteLine();
            Console.Out.WriteLine("Usage: AutopilotMonitor.Agent.V2.exe [options]");
            Console.Out.WriteLine();
            Console.Out.WriteLine("Modes:");
            Console.Out.WriteLine("  --install                         Deploy payload, create Scheduled Task, and start it");
            Console.Out.WriteLine("  --tenant-id <ID>                  Tenant ID for bootstrap-config (used with --install)");
            Console.Out.WriteLine("  (default)                         Run enrollment monitoring");
            Console.Out.WriteLine();
            Console.Out.WriteLine("General options:");
            Console.Out.WriteLine("  --help, -h, -?                    Show this help message");
            Console.Out.WriteLine("  --version                         Print version and exit");
            Console.Out.WriteLine("  --console                         Enable console output (mirrors log to stdout)");
            Console.Out.WriteLine("  --log-level <LEVEL>               Override log level (Info, Debug, Verbose, Trace)");
            Console.Out.WriteLine("  --new-session                     Force a new session ID (delete persisted session)");
            Console.Out.WriteLine("  --keep-logfile                    Preserve log directory after self-destruct cleanup");
            Console.Out.WriteLine("  --no-cleanup                      Disable self-destruct on enrollment completion");
            Console.Out.WriteLine("  --reboot-on-complete              Reboot the device after enrollment completes");
            Console.Out.WriteLine("  --disable-geolocation             Skip geo-location detection");
            Console.Out.WriteLine();
            Console.Out.WriteLine("Authentication:");
            Console.Out.WriteLine("  --bootstrap-token <TOKEN>         Use bootstrap token auth (pre-MDM OOBE phase)");
            Console.Out.WriteLine();
            Console.Out.WriteLine("Await-enrollment mode:");
            Console.Out.WriteLine("  --await-enrollment                Wait for MDM certificate before starting monitoring");
            Console.Out.WriteLine("  --await-enrollment-timeout <MIN>  Timeout in minutes (default: 480)");
            Console.Out.WriteLine();
            Console.Out.WriteLine("Overrides:");
            Console.Out.WriteLine("  --api-url <URL>                   Override backend API base URL (alias: --backend-api)");
            Console.Out.WriteLine("  --ime-log-path <PATH>             Override IME logs directory");
            Console.Out.WriteLine("  --ime-match-log <PATH>            Write matched IME log lines to file (debug)");
        }

        private static void PrintVersion()
        {
            Console.Out.WriteLine(GetAgentVersion());
        }

        private static string GetAgentVersion()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info)) return info;

                var file = asm.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
                if (!string.IsNullOrWhiteSpace(file)) return file;

                return asm.GetName().Version?.ToString() ?? "0.0.0.0";
            }
            catch
            {
                return "0.0.0.0";
            }
        }
    }
}
