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
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
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
    ///   <item>Resolve TenantId (registry → bootstrap-config.json fallback)</item>
    ///   <item>Build <see cref="AgentConfiguration"/> (CLI args + persisted bootstrap / await-enrollment config)</item>
    ///   <item>Get/create SessionId via <see cref="SessionIdPersistence"/></item>
    ///   <item><see cref="CheckEnrollmentCompleteMarker"/> — file-based enrollment-complete-marker detection + cleanup retry</item>
    ///   <item><see cref="CheckSessionAgeEmergencyBreak"/> — absolute session-age watchdog</item>
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
        private const string DefaultStateSubdirectory = "State";
        private const string DefaultSpoolSubdirectory = "Spool";
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
            // PR3-A3: empty args looked broken (`Command line: `). Make the no-args case explicit.
            var formattedArgs = FormatArgsForLog(args);
            logger.Info($"Command line: {(string.IsNullOrEmpty(formattedArgs) ? "(no args)" : formattedArgs)}");

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
            var stateSubdir = Path.Combine(dataDirectory, DefaultStateSubdirectory);
            var transportDir = Path.Combine(dataDirectory, DefaultSpoolSubdirectory);

            // Phase 1+2 (previous-exit, persisted-config, TenantId, AgentConfig, SessionId,
            // completion-marker / emergency-break guards, optional --await-enrollment cert wait)
            // is encapsulated in AgentBootstrap; see Runtime/AgentBootstrap.cs for the V1-parity
            // exit-code mapping (0 = guard handled, 2 = no TenantId, 3 = await timeout).
            var bootstrap = Runtime.AgentBootstrap.Run(args, logger, dataDirectory, logDirectory, stateSubdir, consoleMode);
            if (bootstrap.ShouldExit)
            {
                return bootstrap.ExitCode;
            }

            var agentConfig = bootstrap.AgentConfig;
            var sessionPersistence = bootstrap.SessionPersistence;
            var previousExit = bootstrap.PreviousExit;
            var isWhiteGloveResume = bootstrap.IsWhiteGloveResume;
            var cleanupServiceFactory = bootstrap.CleanupServiceFactory;

            // Phase 3 (BackendApiClient + reporters + auth-failure tracker) is encapsulated
            // in BackendClientFactory; see Runtime/BackendClientFactory.cs.
            var auth = Runtime.BackendClientFactory.BuildAuthClients(agentConfig, GetAgentVersion(), logger);
            var backendApiClient = auth.BackendApiClient;
            var distressReporter = auth.DistressReporter;
            var emergencyReporter = auth.EmergencyReporter;
            var authFailureTracker = auth.AuthFailureTracker;

            // Phase 4 (RemoteConfig fetch + Merge + tracker/logger refresh + binary-integrity
            // verification + bootstrap-config cleanup) is encapsulated in AgentRuntimeConfig.
            // See Runtime/AgentRuntimeConfig.cs.
            var runtimeConfig = Runtime.AgentRuntimeConfig.Resolve(agentConfig, auth, GetAgentVersion(), consoleMode, logger);
            var remoteConfigService = runtimeConfig.RemoteConfigService;
            var remoteConfig = runtimeConfig.RemoteConfig;
            var configMergeResult = runtimeConfig.MergeResult;

            // Phase 5 (mTLS HttpClient + BackendTelemetryUploader) is encapsulated in
            // BackendClientFactory. Construction failures map to V1-parity exit codes
            // (4 = mTLS, 5 = uploader). See Runtime/BackendClientFactory.cs.
            var telemetry = Runtime.BackendClientFactory.BuildTelemetryClients(agentConfig, auth, GetAgentVersion(), logger);
            if (telemetry.ShouldExit)
            {
                return telemetry.ExitCode;
            }
            var mtlsHttpClient = telemetry.MtlsHttpClient;
            var uploader = telemetry.Uploader;

            // Phase 6 (POST /api/agent/register-session with retry + outcome-based exit-code
            // mapping + on-failure client disposal) is encapsulated in BackendSessionRegistration.
            // Exit codes: 6 = AuthFailed, 7 = anything else. See Runtime/BackendSessionRegistration.cs.
            var registration = Runtime.BackendSessionRegistration.Register(
                agentConfig, auth, mtlsHttpClient, GetAgentVersion(), consoleMode, logger);
            if (registration.ShouldExit)
            {
                return registration.ExitCode;
            }
            var registrationResult = registration.Registration;

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

            // P1 fix: tenant-controlled telemetry cadence knobs (UploadIntervalSeconds /
            // MaxBatchSize) were merged into agentConfig by RemoteConfigMerger but never
            // reached the orchestrator. Read them here, clamp to safe bounds, and pass via
            // the orchestrator constructor so a tenant change actually takes effect on the
            // next agent run. Bounds are deliberately wide — they exist only to guard
            // against typos / zero values, not to enforce policy. Initial-apply only;
            // there is no V2 hot-reload path for these knobs because there is no periodic
            // remote-config refresh outside the rotate_config ServerAction (which itself
            // does not re-merge into agentConfig today).
            var drainInterval = TimeSpan.FromSeconds(ClampUploadIntervalSeconds(agentConfig.UploadIntervalSeconds));
            var uploadBatchSize = ClampUploadBatchSize(agentConfig.MaxBatchSize);
            logger.Info(
                $"Telemetry cadence: drainInterval={drainInterval.TotalSeconds:F0}s, " +
                $"uploadBatchSize={uploadBatchSize} " +
                $"(remote raw values: UploadIntervalSeconds={agentConfig.UploadIntervalSeconds}, " +
                $"MaxBatchSize={agentConfig.MaxBatchSize}).");

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
                drainInterval: drainInterval,
                agentMaxLifetime: agentMaxLifetime,
                uploadBatchSize: uploadBatchSize))
            {
                using (var shutdown = new ManualResetEventSlim(false))
                using (var shutdownComplete = new ManualResetEventSlim(false))
                {
                    // M4.6.δ — Analyzer manager. Single-rail refactor (plan §5.7): the manager
                    // now takes an InformationalEventPost so LocalAdmin / SoftwareInventory /
                    // IntegrityBypass analyzer events flow through the same ingress pipe as
                    // every other telemetry source. Construction is deferred into
                    // orchestrator.Start's onIngressReady hook so lifecyclePost is non-null at
                    // construction time. RunStartup fires after orchestrator.Start; RunShutdown
                    // is wired into the termination handler so it runs before diagnostics upload.
                    AgentAnalyzerManager analyzerManager = null;

                    // M4.6.β — the peripheral termination sequence (FinalStatus + SummaryDialog
                    // + diagnostics upload + enrollment-complete.marker + CleanupService) lives
                    // in Program.cs, not in the kernel. Declared here; constructed inside
                    // orchestrator.Start's onIngressReady hook (plan §5.3) so the live
                    // InformationalEventPost backs its telemetry.
                    EnrollmentTerminationHandler terminationHandler = null;

                    // ServerActionDispatcher — constructed after orchestrator.Start (see below) so
                    // the live lifecyclePost can back its telemetry. Declared here so the terminate
                    // callback + WireTelemetryServerResponse reach it via the local scope.
                    ServerActionDispatcher serverActionDispatcher = null;

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

                    // Single-rail refactor (plan §5.1) — lifecycle events (agent_started,
                    // agent_version_check, agent_unrestricted_mode_changed, enrollment_failed,
                    // agent_shutdown) flow through InformationalEventPost → SignalIngress →
                    // Reducer → EmitEventTimelineEntry effect → EventTimelineEmitter. The post
                    // instance is constructed inside orchestrator.Start's onIngressReady callback
                    // (ingress is not alive before Start); later-firing handlers (max-lifetime
                    // watchdog, auth-failure watchdog) capture this variable by reference so they
                    // see the live helper when their event fires.
                    InformationalEventPost lifecyclePost = null;

                    // V1 parity — on the max-lifetime watchdog firing, emit an explicit
                    // `enrollment_failed` event with failureType=agent_timeout BEFORE the
                    // regular termination path runs. See Runtime/LifecycleEmitters.cs.
                    var maxLifetimeEmitter = Runtime.LifecycleEmitters.CreateMaxLifetimeEmitter(
                        getLifecyclePost: () => lifecyclePost,
                        agentConfig: agentConfig,
                        agentStartTimeUtc: agentStartTimeUtc,
                        logger: logger);
                    orchestrator.Terminated += maxLifetimeEmitter;
                    // terminationHandler is constructed inside orchestrator.Start's onIngressReady
                    // hook below, so this wrapper null-checks the captured variable. Terminated
                    // cannot fire until the decision loop has at least one posted signal, and that
                    // loop is started *inside* Start — by the time the first signal is processed,
                    // the hook has already run synchronously and terminationHandler is non-null.
                    EventHandler<EnrollmentTerminatedEventArgs> terminatedDispatch = (s, e) =>
                    {
                        if (terminationHandler == null)
                        {
                            logger.Warning("orchestrator.Terminated fired before terminationHandler constructed — ignoring.");
                            return;
                        }
                        terminationHandler.Handle(s, e);
                    };
                    orchestrator.Terminated += terminatedDispatch;

                    // Auth-failure watchdog: when MaxAuthFailures / AuthFailureTimeoutMinutes
                    // is exceeded the agent must shut down cleanly instead of hammering a
                    // backend that has definitely said no. See Runtime/LifecycleEmitters.cs.
                    var authThresholdHandler = Runtime.LifecycleEmitters.CreateAuthThresholdHandler(
                        getLifecyclePost: () => lifecyclePost,
                        agentConfig: agentConfig,
                        signalShutdown: () => shutdown.Set(),
                        logger: logger);
                    authFailureTracker.ThresholdExceeded += authThresholdHandler;

                    try
                    {
                        // Pre-collector hook emits the lifecycle events (agent_started first so
                        // it is Seq=1 on the wire, then version-check, then the unrestricted-mode
                        // audit). These must land on the signal log before any collector-generated
                        // signal — fixes the Seq=13 ordering regression from the V2 parity audit
                        // (plan Parity Issue #1).
                        orchestrator.Start(ingress =>
                        {
                            lifecyclePost = new InformationalEventPost(ingress, SystemClock.Instance, logger);
                            Runtime.LifecycleEmitters.EmitAgentStarted(lifecyclePost, agentConfig, previousExit, GetAgentVersion(), logger);
                            Runtime.LifecycleEmitters.EmitVersionCheckIfAny(lifecyclePost, agentConfig, logger);
                            Runtime.LifecycleEmitters.EmitUnrestrictedModeAuditIfChanged(lifecyclePost, agentConfig, configMergeResult, logger);

                            // Single-rail refactor (plan §5.7) — AgentAnalyzerManager emits through
                            // the same InformationalEventPost. Constructed inside this hook so
                            // lifecyclePost is guaranteed non-null by the time RunStartup (below,
                            // after Start returns) and RunShutdown (via terminationHandler) call
                            // into the three analyzers.
                            analyzerManager = new AgentAnalyzerManager(
                                configuration: agentConfig,
                                logger: logger,
                                post: lifecyclePost,
                                analyzerConfig: remoteConfig.Analyzers);

                            // Single-rail refactor (plan §5.3) — EnrollmentTerminationHandler emits
                            // through the same InformationalEventPost. Constructed inside this hook
                            // so lifecyclePost is guaranteed non-null; the terminated-dispatch wrapper
                            // registered above picks this instance up via closure capture.
                            terminationHandler = new EnrollmentTerminationHandler(
                                configuration: agentConfig,
                                logger: logger,
                                stateDirectory: stateSubdir,
                                agentStartTimeUtc: agentStartTimeUtc,
                                currentStateAccessor: () => orchestrator.CurrentState,
                                // F5 (debrief 7dd4e593) — pass the deduped phase-snapshot+live
                                // union so DeviceSetup apps cleared from _packageStates on the
                                // AccountSetup transition still appear in the SummaryDialog and
                                // app_tracking_summary event.
                                packageStatesAccessor: () => componentFactory.AllKnownPackageStates,
                                cleanupServiceFactory: () => new CleanupService(agentConfig, logger),
                                uploadDiagnosticsAsync: uploadDiagnosticsAsync,
                                signalShutdown: () => shutdown.Set(),
                                analyzerManager: analyzerManager,
                                post: lifecyclePost,
                                sessionPersistence: sessionPersistence,
                                // Plan §5 Fix 4 — per-app timing snapshot for FinalStatusBuilder +
                                // app_tracking_summary emission. Null-safe via the handler's default.
                                appTimingsAccessor: () => componentFactory.ImeAppTimings,
                                agentVersion: GetAgentVersion(),
                                // Stop periodic collectors (PerformanceCollector,
                                // AgentSelfMetricsCollector, …) before the diagnostics ZIP is
                                // built, so no late `performance_snapshot` slips in after
                                // `diagnostics_collecting`. Idempotent on the orchestrator side
                                // — the full Stop() call later is a no-op for hosts.
                                stopPeripheralCollectors: () => orchestrator.StopCollectorHosts());

                            // ServerActionDispatcher (plan §5.3) — constructed inside this hook so
                            // lifecyclePost + terminationHandler are guaranteed non-null. Logic
                            // (rotate_config / upload_diagnostics / terminate_session callbacks +
                            // the synchronous-shutdown wait) lives in Runtime/ServerControlPlane.cs.
                            serverActionDispatcher = Runtime.ServerControlPlane.BuildDispatcher(
                                agentConfig: agentConfig,
                                orchestrator: orchestrator,
                                terminationHandler: terminationHandler,
                                remoteConfigService: remoteConfigService,
                                diagnosticsService: diagnosticsService,
                                shutdown: shutdown,
                                shutdownComplete: shutdownComplete,
                                post: lifecyclePost,
                                logger: logger);
                        });

                        // M4.6.ε — BackendTelemetryUploader response-plumbing. The orchestrator
                        // parses DeviceBlocked / DeviceKillSignal / AdminAction / Actions out of
                        // the 2xx response body and raises ServerResponseReceived; we translate
                        // those into ServerActions and dispatch. MUST be wired AFTER Start() —
                        // orchestrator.Transport throws before Start because the
                        // TelemetryUploadOrchestrator is constructed inside Start at step 311.
                        Runtime.ServerControlPlane.Wire(
                            orchestrator,
                            serverActionDispatcher,
                            lifecyclePost,
                            () => terminationHandler,
                            agentConfig,
                            shutdownComplete,
                            logger);

                        // WhiteGlove Part-2 resume: EnrollmentOrchestrator.Start already posts
                        // SessionRecovered, which triggers HandleWhiteGlovePart1To2Bridge — that
                        // reducer effect emits whiteglove_resumed on the timeline. We only need
                        // to clear the persisted marker here.
                        if (isWhiteGloveResume)
                        {
                            try { sessionPersistence.ClearWhiteGloveComplete(logger); }
                            catch (Exception ex) { logger.Debug($"ClearWhiteGloveComplete threw: {ex.Message}"); }
                        }

                        // Reboot mid-session: post SystemRebootObserved so the reducer records
                        // the fact and emits the system_reboot_detected timeline entry.
                        if (string.Equals(previousExit?.ExitType, "reboot_kill", StringComparison.OrdinalIgnoreCase))
                        {
                            Runtime.LifecycleEmitters.PostSystemRebootObserved(orchestrator.IngressSink, previousExit, logger);
                        }

                        // V2 parity — post SessionStarted so the reducer establishes the session anchor
                        // (HandleSessionStartedV1 in DecisionEngine.Shared.cs). Skipped on:
                        //   - WhiteGlove Part-2 resume: EnrollmentOrchestrator.Start already posts
                        //     SessionRecovered, which triggers HandleWhiteGlovePart1To2Bridge.
                        //   - Admin preemption: the AdminPreemptionDetected signal below drives the
                        //     session straight to a terminal stage; SessionStarted first would be noise.
                        if (!isWhiteGloveResume && string.IsNullOrEmpty(registrationResult.AdminAction))
                        {
                            Runtime.LifecycleEmitters.PostSessionStarted(orchestrator.IngressSink, registrationResult, agentConfig, GetAgentVersion(), logger);
                        }

                        // V1 parity (MonitoringService.cs:388-413 "ADMIN OVERRIDE on startup") —
                        // if the register-session response carried an AdminAction the operator
                        // has already marked the session terminal via the portal. Post
                        // AdminPreemptionDetected so the reducer transitions Stage to Completed /
                        // Failed and emits the enrollment_complete/_failed timeline event as a
                        // side effect. The orchestrator's DecisionStepProcessor picks up the
                        // terminal stage and raises Terminated, which runs the termination
                        // pipeline (cleanup + summary + self-destruct) through the subscribed
                        // handler — no direct synthesis needed.
                        if (!string.IsNullOrEmpty(registrationResult.AdminAction))
                        {
                            Runtime.LifecycleEmitters.PostAdminPreemption(orchestrator.IngressSink, registrationResult, logger);
                        }

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
                                // lifecyclePost is non-null here — orchestrator.Start's hook
                                // has already run synchronously (see line ~700). The probes
                                // emit device_location / timezone_auto_set / ntp_time_check /
                                // agent_trace through the single-rail pipe, preserving their
                                // Source labels via the InformationalEventPost contract.
                                await StartupEnvironmentProbes
                                    .RunAsync(agentConfig, logger, lifecyclePost)
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
                        orchestrator.Terminated -= terminatedDispatch;
                        orchestrator.Terminated -= maxLifetimeEmitter;
                        authFailureTracker.ThresholdExceeded -= authThresholdHandler;

                        try { orchestrator.Stop(); }
                        catch (Exception ex) { logger.Error("Orchestrator stop failed.", ex); }

                        try { mtlsHttpClient.Dispose(); } catch { }
                        try { backendApiClient.Dispose(); } catch { }

                        // Plan §6.2 synchronous-shutdown — release any ingest-dispatcher thread
                        // currently parked in the terminate callback (see onTerminateRequested
                        // above). Signalled AFTER orchestrator.Stop and client disposal so the
                        // ingest thread only returns once no more HTTP work can happen.
                        shutdownComplete.Set();
                    }
                }
            }

            logger.Info("AutopilotMonitor.Agent.V2 stopped cleanly.");
            return 0;
        }

        // Clamp bounds for the tenant-controlled telemetry cadence knobs. The orchestrator
        // throws on non-positive values; the upper bounds are sanity guards so a typo in
        // tenant config can't push the drain to an absurd cadence (e.g. 1 sec hammer-poll
        // or hour-long gaps that hide live UI from operators).
        private const int MinUploadIntervalSeconds = 5;
        private const int MaxUploadIntervalSeconds = 300;
        private const int DefaultUploadIntervalSeconds = 30;
        private const int MinUploadBatchSize = 1;
        private const int MaxUploadBatchSize = 500;
        private const int DefaultUploadBatchSize = 100;

        private static int ClampUploadIntervalSeconds(int requested)
        {
            if (requested <= 0) return DefaultUploadIntervalSeconds;
            if (requested < MinUploadIntervalSeconds) return MinUploadIntervalSeconds;
            if (requested > MaxUploadIntervalSeconds) return MaxUploadIntervalSeconds;
            return requested;
        }

        private static int ClampUploadBatchSize(int requested)
        {
            if (requested <= 0) return DefaultUploadBatchSize;
            if (requested < MinUploadBatchSize) return MinUploadBatchSize;
            if (requested > MaxUploadBatchSize) return MaxUploadBatchSize;
            return requested;
        }

        // ---------------------------------------------------------------- Configuration

        internal static AgentConfiguration BuildAgentConfiguration(
            string[] args,
            string tenantId,
            string sessionId,
            BootstrapConfigFile bootstrapConfig,
            AwaitEnrollmentConfigFile awaitConfig)
        {
            var apiBaseUrl = GetArgValue(args, "--api-url", "--backend-api") ?? Constants.ApiBaseUrl;
            var imeLogPathOverride = GetArgValue(args, "--ime-log-path");
            var imeMatchLogPath = GetArgValue(args, "--ime-match-log");

            // Dev / test — IME log replay (V1 compat mode). Feeds ImeLogTracker.SimulationMode
            // + SpeedFactor so recorded raw IME logs are replayed at an accelerated rate; signal
            // timestamps + ingress ordinals are regenerated as the replay runs.
            var replayLogDir = GetArgValue(args, "--replay-log-dir");
            var replaySpeedFactorRaw = GetArgValue(args, "--replay-speed-factor");
            var replaySpeedFactor = 50.0;
            if (!string.IsNullOrEmpty(replaySpeedFactorRaw)
                && double.TryParse(replaySpeedFactorRaw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedSpeed)
                && parsedSpeed > 0)
            {
                replaySpeedFactor = parsedSpeed;
            }

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
                ReplayLogDir = replayLogDir,
                ReplaySpeedFactor = replaySpeedFactor,
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
            Console.Out.WriteLine();
            Console.Out.WriteLine("Dev / Test:");
            Console.Out.WriteLine("  --replay-log-dir <PATH>           Replay IME logs from this directory (simulation mode)");
            Console.Out.WriteLine("  --replay-speed-factor <N>         Time-compression factor for log replay (default: 50)");
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
