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
    ///   <item>Get/create SessionId via <see cref="SessionIdPersistence"/> — <b>before</b> the guards, matching Legacy boot order. The ghost-restart guard keys on <c>session.id</c> absence to detect "self-destruct ran but Scheduled Task survived"; creating the session after the guard would misdiagnose every first-run-after-install as a ghost restart and trigger self-destruct before the remote-config fetch.</item>
    ///   <item><see cref="CheckEnrollmentCompleteMarker"/> — ghost-restart detection + cleanup retry</item>
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
            var stateSubdir = Path.Combine(dataDirectory, DefaultStateSubdirectory);
            var transportDir = Path.Combine(dataDirectory, DefaultSpoolSubdirectory);

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

            var agentConfig = BuildAgentConfiguration(args, tenantId, sessionId: null, bootstrapConfig, awaitConfig);

            // Create/recover SessionId BEFORE the startup guards. Legacy-parity boot order —
            // the ghost-restart guard below keys on session.id absence to detect
            // "self-destruct ran but Scheduled Task survived". Creating the session after the
            // guard would misdiagnose every first-run-after-install (Deployed registry marker
            // set by --install, no session.id yet) as a ghost restart and trigger
            // ExecuteSelfDestruct before the remote-config fetch, deleting the ProgramData
            // directory on every fresh deployment.
            var sessionPersistence = new SessionIdPersistence(dataDirectory);
            if (args.Contains("--new-session"))
            {
                sessionPersistence.Delete(logger);
                logger.Info("--new-session: cleared persisted SessionId.");
            }
            // Snapshot the WhiteGlove-resume state BEFORE GetOrCreate: on Part-2 resume we want
            // to emit the whiteglove_resumed event AFTER orchestrator.Start. Reading the marker
            // up front avoids racing any downstream clear. V1 parity — the Part-2 detection is
            // marker-based, and the agent announces resume on the session timeline so that
            // dashboards can correlate the session's two boots.
            var isWhiteGloveResume = sessionPersistence.IsWhiteGloveResume();
            agentConfig.SessionId = sessionPersistence.GetOrCreate(logger);

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

            // Central observer for consecutive 401/403 responses. Initialised with the CLI/bootstrap
            // defaults on AgentConfiguration; UpdateLimits is called after RemoteConfigMerger.Merge
            // so tenant-policy overrides take effect. ThresholdExceeded is wired below, once the
            // shutdown signal is available, to trigger a clean agent exit when the limit is hit.
            // V1 parity — the distress reporter is plumbed in at construction so the tracker is
            // the single dispatch point for auth-failure distress (first failure only).
            var authFailureTracker = new AuthFailureTracker(
                maxFailures: agentConfig.MaxAuthFailures,
                timeoutMinutes: agentConfig.AuthFailureTimeoutMinutes,
                clock: SystemClock.Instance,
                logger: logger,
                distressReporter: distressReporter);

            var remoteConfigService = new RemoteConfigService(
                backendApiClient, agentConfig.TenantId, logger, emergencyReporter, distressReporter, authFailureTracker);
            var remoteConfig = remoteConfigService.FetchConfigAsync().GetAwaiter().GetResult();

            // Project remote tenant-controlled knobs onto the runtime AgentConfiguration so that
            // downstream consumers (CleanupService, SummaryDialogLauncher, StartupEnvironmentProbes,
            // DiagnosticsPackageService, EnrollmentTerminationHandler, the logger, the watchdog)
            // actually respect the tenant admin settings. V1 parity — remote wins
            // unconditionally for every knob that has a 1:1 mapping. CLI flags seed the initial
            // AgentConfiguration in BuildAgentConfiguration and then yield to tenant policy.
            var configMergeResult = RemoteConfigMerger.Merge(agentConfig, remoteConfig, logger);

            // Refresh tracker ceilings with the tenant-specific values we just merged in.
            authFailureTracker.UpdateLimits(agentConfig.MaxAuthFailures, agentConfig.AuthFailureTimeoutMinutes);

            logger.SetLogLevel(agentConfig.LogLevel);

            // Propagate the backend-expected SHA so the runtime hash-mismatch trigger (M4.6.α
            // continues this wire; actual runtime trigger will be wired via ServerActionDispatcher
            // in M4.6.β) has the up-to-date integrity hash. Also refresh AllowAgentDowngrade.
            if (!string.IsNullOrEmpty(remoteConfig.LatestAgentSha256))
                SelfUpdater.BackendExpectedSha256 = remoteConfig.LatestAgentSha256;

            // Post-config binary-integrity check: verify the running EXE's SHA-256 against the
            // value advertised by the backend. V1 parity — on mismatch we (a) emit an
            // IntegrityCheckFailed emergency report and (b) fire the runtime self-update trigger
            // so the agent auto-heals (SelfUpdater.CheckAndApplyUpdateAsync force-update path).
            // The trigger is single-shot per process (Interlocked guard inside the verifier).
            var agentDirForTrigger = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            Func<string, bool, Task> runtimeSelfUpdateTrigger = (zipHash, downgrade) =>
            {
                if (!string.IsNullOrEmpty(zipHash))
                    SelfUpdater.BackendExpectedSha256 = zipHash;

                return SelfUpdater.CheckAndApplyUpdateAsync(
                    currentVersion: GetAgentVersion(),
                    agentDir: agentDirForTrigger,
                    consoleMode: consoleMode,
                    forceUpdate: true,
                    triggerReason: "runtime_hash_mismatch",
                    downloadTimeoutMsOverride: 60000,
                    allowDowngrade: downgrade);
            };

            var integrityResult = BinaryIntegrityVerifier.Check(
                expectedSha256: remoteConfig.LatestAgentExeSha256,
                logger: logger,
                runtimeSelfUpdateTrigger: runtimeSelfUpdateTrigger,
                zipHash: remoteConfig.LatestAgentSha256,
                allowDowngrade: remoteConfig.AllowAgentDowngrade);
            if (integrityResult.IsMismatch)
            {
                _ = emergencyReporter.TrySendAsync(
                    AgentErrorType.IntegrityCheckFailed,
                    $"Running exe SHA-256 differs from backend-advertised hash. actual={integrityResult.ActualSha256}, expected={integrityResult.ExpectedSha256}");
            }

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
                    agentVersion: GetAgentVersion(),
                    authFailureTracker: authFailureTracker);
            }
            catch (Exception ex)
            {
                logger.Error("BackendTelemetryUploader construction failed.", ex);
                return 5;
            }

            // V1 parity (MonitoringService.Start, MonitoringService.RegisterSessionAsync) —
            // POST /api/agent/register-session with 5-retry exponential backoff (2s/4s/8s/16s)
            // BEFORE the orchestrator starts. Without this call the backend's Sessions table
            // never gets a row for this session, so IncrementSessionEventCountAsync /
            // UpdateSessionStatusAsync silently no-op — events still land in the Events table
            // but session status, phase, admin-overrides and validator reconcile all break.
            // On failure we follow V1's rule: collectors MUST NOT start to prevent orphaned events.
            var registrationResult = SessionRegistrationHelper.RegisterWithRetryAsync(
                apiClient: backendApiClient,
                agentConfig: agentConfig,
                agentVersion: GetAgentVersion(),
                logger: logger,
                authFailureTracker: authFailureTracker,
                emergencyReporter: emergencyReporter).GetAwaiter().GetResult();

            if (registrationResult.Outcome != SessionRegistrationOutcome.Succeeded)
            {
                logger.Error(
                    $"=== SESSION REGISTRATION FAILED ({registrationResult.Outcome}: {registrationResult.ErrorMessage}) — " +
                    "collectors will NOT start to prevent orphaned events. ===");
                if (consoleMode)
                    Console.Error.WriteLine($"FATAL: session registration failed ({registrationResult.Outcome}). Agent exiting.");
                try { mtlsHttpClient?.Dispose(); } catch { }
                try { backendApiClient?.Dispose(); } catch { }
                // Exit code differs so the diag skill can distinguish Auth vs Network in Scheduled-Task history.
                return registrationResult.Outcome == SessionRegistrationOutcome.AuthFailed ? 6 : 7;
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
                    // regular termination path runs. Dashboards + KQL queries key on the
                    // event type + data dictionary to distinguish a genuine enrollment failure
                    // from a timeout-triggered shutdown.
                    EventHandler<EnrollmentTerminatedEventArgs> maxLifetimeEmitter = (s, e) =>
                    {
                        if (e.Reason != EnrollmentTerminationReason.MaxLifetimeExceeded) return;
                        if (lifecyclePost == null)
                        {
                            logger.Warning("enrollment_failed (max_lifetime) suppressed — ingress not ready.");
                            return;
                        }
                        try
                        {
                            var uptimeMin = (DateTime.UtcNow - agentStartTimeUtc).TotalMinutes;
                            // Phase stays Unknown per plan §1.4 phase-invariant — the UI timeline
                            // buckets chronologically into the last-declared phase. This fixes the
                            // legacy violation where enrollment_failed (max_lifetime) carried
                            // Phase=Complete and caused a phantom phase in the UI.
                            lifecyclePost.Emit(new EnrollmentEvent
                            {
                                SessionId = agentConfig.SessionId,
                                TenantId = agentConfig.TenantId,
                                EventType = "enrollment_failed",
                                Severity = EventSeverity.Error,
                                Source = "EnrollmentOrchestrator",
                                Phase = EnrollmentPhase.Unknown,
                                Message = $"Agent max lifetime expired ({uptimeMin:F0} min) — enrollment did not complete in time",
                                Data = new System.Collections.Generic.Dictionary<string, object>
                                {
                                    { "failureType", "agent_timeout" },
                                    { "failureSource", "max_lifetime_timer" },
                                    { "agentUptimeMinutes", Math.Round(uptimeMin, 1) },
                                    { "agentMaxLifetimeMinutes", agentConfig.AgentMaxLifetimeMinutes },
                                    { "stageAtTimeout", e.StageName ?? string.Empty },
                                },
                                ImmediateUpload = true,
                            });
                        }
                        catch (Exception emitEx)
                        {
                            logger.Warning($"enrollment_failed (max_lifetime) emission failed: {emitEx.Message}");
                        }
                    };
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
                    // backend that has definitely said no. Event fires at most once.
                    // V1 parity — emit a structured `agent_shutdown` event with reason=auth_failure
                    // and the full telemetry payload before tripping the shutdown signal so the
                    // backend sees WHY the agent terminated in the session timeline.
                    EventHandler<AuthFailureThresholdEventArgs> authThresholdHandler = (s, e) =>
                    {
                        logger.Error($"Auth-failure threshold exceeded ({e.Reason}) — initiating shutdown.");
                        if (lifecyclePost != null)
                        {
                            try
                            {
                                lifecyclePost.Emit(new EnrollmentEvent
                                {
                                    SessionId = agentConfig.SessionId,
                                    TenantId = agentConfig.TenantId,
                                    EventType = "agent_shutdown",
                                    Severity = EventSeverity.Error,
                                    Source = "AuthFailureTracker",
                                    Phase = EnrollmentPhase.Unknown,
                                    Message = $"Agent shut down after {e.ConsecutiveFailures} consecutive auth failures",
                                    Data = new System.Collections.Generic.Dictionary<string, object>
                                    {
                                        { "reason", "auth_failure" },
                                        { "consecutiveFailures", e.ConsecutiveFailures },
                                        { "firstFailureTime", e.FirstFailureUtc.ToString("o") },
                                        { "maxFailures", agentConfig.MaxAuthFailures },
                                        { "timeoutMinutes", agentConfig.AuthFailureTimeoutMinutes },
                                        { "lastOperation", e.LastOperation ?? string.Empty },
                                        { "lastStatusCode", e.LastStatusCode },
                                        { "thresholdReason", e.Reason ?? string.Empty },
                                    },
                                    ImmediateUpload = true,
                                });
                            }
                            catch (Exception emitEx)
                            {
                                logger.Warning($"agent_shutdown emission failed: {emitEx.Message}");
                            }
                        }
                        else
                        {
                            logger.Warning("agent_shutdown (auth_failure) suppressed — ingress not ready.");
                        }

                        shutdown.Set();
                    };
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
                            lifecyclePost = new InformationalEventPost(ingress, SystemClock.Instance);
                            EmitAgentStartedEvent(lifecyclePost, agentConfig, previousExit, logger);
                            EmitVersionCheckEventIfAny(lifecyclePost, agentConfig, logger);
                            EmitUnrestrictedModeAuditIfChanged(lifecyclePost, agentConfig, configMergeResult, logger);

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
                                packageStatesAccessor: () => componentFactory.ImePackageStates,
                                cleanupServiceFactory: () => new CleanupService(agentConfig, logger),
                                uploadDiagnosticsAsync: uploadDiagnosticsAsync,
                                signalShutdown: () => shutdown.Set(),
                                analyzerManager: analyzerManager,
                                post: lifecyclePost,
                                sessionPersistence: sessionPersistence);

                            // Single-rail refactor (plan §5.3) — ServerActionDispatcher emits through
                            // the same InformationalEventPost. Constructed inside this hook so
                            // lifecyclePost is guaranteed non-null; the dispatcher is only wired into
                            // the telemetry response-path below (WireTelemetryServerResponse), which
                            // runs strictly after Start returns.
                            serverActionDispatcher = new ServerActionDispatcher(
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
                                post: lifecyclePost);
                        });

                        // M4.6.ε — BackendTelemetryUploader response-plumbing. The orchestrator parses
                        // DeviceBlocked / DeviceKillSignal / AdminAction / Actions out of the 2xx
                        // response body (see BackendTelemetryUploader.TryReadControlSignalsAsync) and
                        // raises ServerResponseReceived. We translate those into ServerActions and
                        // dispatch. Legacy parity with IngestEventsResponse synthesis in
                        // EventUploadOrchestrator.OnEventsUploaded().
                        //
                        // MUST be wired AFTER Start() — orchestrator.Transport throws
                        // InvalidOperationException before Start() because the
                        // TelemetryUploadOrchestrator is constructed inside Start() at step 311.
                        WireTelemetryServerResponse(orchestrator, serverActionDispatcher, logger);

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
                            PostSystemRebootObservedSignal(orchestrator.IngressSink, previousExit, logger);
                        }

                        // V2 parity — post SessionStarted so the reducer establishes the session anchor
                        // (HandleSessionStartedV1 in DecisionEngine.Shared.cs). Skipped on:
                        //   - WhiteGlove Part-2 resume: EnrollmentOrchestrator.Start already posts
                        //     SessionRecovered, which triggers HandleWhiteGlovePart1To2Bridge.
                        //   - Admin preemption: the AdminPreemptionDetected signal below drives the
                        //     session straight to a terminal stage; SessionStarted first would be noise.
                        if (!isWhiteGloveResume && string.IsNullOrEmpty(registrationResult.AdminAction))
                        {
                            PostSessionStartedSignal(orchestrator.IngressSink, registrationResult, agentConfig, logger);
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
                            PostAdminPreemptionSignal(orchestrator.IngressSink, registrationResult, logger);
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

        /// <summary>
        /// V1 parity — when <see cref="RemoteConfigMerger"/> flips the tenant-controlled
        /// <c>UnrestrictedMode</c> guardrail, surface the transition as an auditable event on
        /// the session timeline so operators can correlate subsequent gather-rule exec with the
        /// elevated policy. The V1 code lives in
        /// <c>MonitoringService.AuditUnrestrictedModeChange</c>.
        /// </summary>
        private static void EmitUnrestrictedModeAuditIfChanged(
            InformationalEventPost post,
            AgentConfiguration agentConfig,
            RemoteConfigMergeResult mergeResult,
            AgentLogger logger)
        {
            if (mergeResult == null || !mergeResult.UnrestrictedModeChanged) return;

            try
            {
                var newValue = mergeResult.NewUnrestrictedMode;
                logger.Warning(
                    $"UnrestrictedMode changed: {mergeResult.OldUnrestrictedMode} → {newValue}. Emitting audit event.");

                post.Emit(new EnrollmentEvent
                {
                    SessionId = agentConfig.SessionId,
                    TenantId = agentConfig.TenantId,
                    EventType = "agent_unrestricted_mode_changed",
                    Severity = newValue ? EventSeverity.Warning : EventSeverity.Info,
                    Source = "RemoteConfigMerger",
                    Phase = EnrollmentPhase.Unknown,
                    Message = newValue
                        ? "Agent unrestricted mode ENABLED — gather rules can now execute without AllowList checks"
                        : "Agent unrestricted mode disabled — gather rules revert to AllowList checks",
                    Data = new System.Collections.Generic.Dictionary<string, object>
                    {
                        { "oldValue", mergeResult.OldUnrestrictedMode },
                        { "newValue", newValue },
                        { "changedAtUtc", DateTime.UtcNow.ToString("o") },
                    },
                    ImmediateUpload = true,
                });
            }
            catch (Exception ex)
            {
                logger.Warning($"EmitUnrestrictedModeAuditIfChanged: emission failed: {ex.Message}");
            }
        }

        /// <summary>
        /// V2 parity — post <see cref="DecisionSignalKind.AdminPreemptionDetected"/> when the
        /// register-session response carried an <c>AdminAction</c>. The reducer handler at
        /// DecisionEngine.Shared.cs#HandleAdminPreemptionDetectedV1 transitions Stage to the
        /// terminal state and emits the enrollment_complete/_failed telemetry event; the
        /// orchestrator's DecisionStepProcessor then raises the Terminated event, which the
        /// subscribed <c>EnrollmentTerminationHandler</c> picks up — no direct synthesis needed.
        /// </summary>
        private static void PostAdminPreemptionSignal(
            ISignalIngressSink ingressSink,
            SessionRegistrationResult registrationResult,
            AgentLogger logger)
        {
            try
            {
                var adminOutcome = registrationResult.AdminAction; // "Succeeded" | "Failed"
                logger.Warning(
                    $"=== ADMIN OVERRIDE on startup: session already marked as {adminOutcome} by administrator — posting AdminPreemptionDetected signal ===");

                ingressSink.Post(
                    kind: DecisionSignalKind.AdminPreemptionDetected,
                    occurredAtUtc: DateTime.UtcNow,
                    sourceOrigin: "register_session_response",
                    evidence: new Evidence(
                        kind: EvidenceKind.Synthetic,
                        identifier: $"admin_preemption:{adminOutcome}",
                        summary: $"Operator marked session as {adminOutcome} via portal before agent startup."),
                    payload: new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["adminOutcome"] = adminOutcome,
                    });
            }
            catch (Exception ex)
            {
                logger.Warning($"AdminPreemptionDetected post failed: {ex.Message}");
            }
        }

        /// <summary>
        /// V2 parity — post a <see cref="DecisionSignalKind.SessionStarted"/> signal with the
        /// tenant-registered session metadata (EnrollmentType / IsHybridJoin / ValidatedBy) so the
        /// reducer's session-anchor handler runs. Without this the DecisionState.Stage stays at
        /// the initial value and subsequent raw signals (ESP / Hello) see an uninitialised session.
        /// </summary>
        internal static void PostSessionStartedSignal(
            ISignalIngressSink ingressSink,
            SessionRegistrationResult registrationResult,
            AgentConfiguration agentConfig,
            AgentLogger logger)
        {
            try
            {
                var payload = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["enrollmentType"] = EnrollmentRegistryDetector.DetectEnrollmentType(),
                    ["isHybridJoin"] = EnrollmentRegistryDetector.DetectHybridJoin() ? "true" : "false",
                    ["validatedBy"] = registrationResult.ValidatedBy.ToString(),
                    ["agentVersion"] = GetAgentVersion(),
                    ["isBootstrapSession"] = agentConfig.UseBootstrapTokenAuth ? "true" : "false",
                };

                var evidence = new Evidence(
                    kind: EvidenceKind.Synthetic,
                    identifier: "register_session_success",
                    summary: "Session registration handshake succeeded; posting SessionStarted anchor for reducer.");

                ingressSink.Post(
                    kind: DecisionSignalKind.SessionStarted,
                    occurredAtUtc: DateTime.UtcNow,
                    sourceOrigin: "Program.RunAgent",
                    evidence: evidence,
                    payload: payload);

                logger.Debug($"SessionStarted signal posted (validatedBy={registrationResult.ValidatedBy}, enrollmentType={payload["enrollmentType"]}).");
            }
            catch (Exception ex)
            {
                logger.Warning($"SessionStarted post failed: {ex.Message}");
            }
        }

        /// <summary>
        /// V1 parity — fire-and-forget <c>agent_started</c> event emitted after
        /// <see cref="EnrollmentOrchestrator.Start"/>. Carries a snapshot of the boot classification
        /// (<paramref name="previousExit"/>) and the tenant-influenced runtime knobs so dashboards
        /// can classify crash-loops, backend-rejected sessions and forced self-destruct runs.
        /// Phase stays <see cref="EnrollmentPhase.Unknown"/> — the event is NOT a phase declaration.
        /// </summary>
        private static void EmitAgentStartedEvent(
            InformationalEventPost post,
            AgentConfiguration agentConfig,
            PreviousExitSummary previousExit,
            AgentLogger logger)
        {
            try
            {
                var data = new System.Collections.Generic.Dictionary<string, object>
                {
                    { "agentVersion", GetAgentVersion() },
                    { "commandLineArgs", agentConfig.CommandLineArgs ?? string.Empty },
                    { "isBootstrapSession", agentConfig.UseBootstrapTokenAuth },
                    { "awaitEnrollment", agentConfig.AwaitEnrollment },
                    { "selfDestructOnComplete", agentConfig.SelfDestructOnComplete },
                    { "certAuth", !agentConfig.UseBootstrapTokenAuth },
                    { "agentMaxLifetimeMinutes", agentConfig.AgentMaxLifetimeMinutes },
                    { "diagnosticsUploadMode", agentConfig.DiagnosticsUploadMode ?? "Off" },
                    { "previousExitType", previousExit?.ExitType ?? "unknown" },
                    { "unrestrictedMode", agentConfig.UnrestrictedMode },
                };

                if (!string.IsNullOrEmpty(previousExit?.CrashExceptionType))
                    data["previousCrashException"] = previousExit.CrashExceptionType;

                if (previousExit?.LastBootUtc.HasValue == true)
                    data["previousBootUtc"] = previousExit.LastBootUtc.Value.ToString("o");

                post.Emit(new EnrollmentEvent
                {
                    SessionId = agentConfig.SessionId,
                    TenantId = agentConfig.TenantId,
                    EventType = "agent_started",
                    Severity = EventSeverity.Info,
                    Source = "AutopilotMonitor.Agent.V2",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"Agent v{GetAgentVersion()} started (previousExit={previousExit?.ExitType ?? "unknown"}).",
                    Data = data,
                    ImmediateUpload = true,
                });
            }
            catch (Exception ex)
            {
                logger.Warning($"agent_started emission failed: {ex.Message}");
            }
        }

        /// <summary>
        /// V2 parity — post <see cref="DecisionSignalKind.SystemRebootObserved"/> when the prior
        /// agent process was terminated by an OS reboot. The reducer handler at
        /// DecisionEngine.Edge.cs#HandleSystemRebootObservedV1 records the reboot fact on the
        /// state (used by the WhiteGlove reboot-observed scoring weight, plan §2.4) and emits
        /// the <c>system_reboot_detected</c> telemetry event as a side effect.
        /// </summary>
        private static void PostSystemRebootObservedSignal(
            ISignalIngressSink ingressSink,
            PreviousExitSummary previousExit,
            AgentLogger logger)
        {
            try
            {
                var payload = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["previousExitType"] = previousExit?.ExitType ?? string.Empty,
                    ["lastBootUtc"] = previousExit?.LastBootUtc?.ToString("o") ?? string.Empty,
                };

                ingressSink.Post(
                    kind: DecisionSignalKind.SystemRebootObserved,
                    occurredAtUtc: DateTime.UtcNow,
                    sourceOrigin: "Program.DetectPreviousExit",
                    evidence: new Evidence(
                        kind: EvidenceKind.Synthetic,
                        identifier: "previous_exit_reboot_kill",
                        summary: $"Prior agent process terminated by OS reboot (exitType={previousExit?.ExitType})."),
                    payload: payload);
            }
            catch (Exception ex)
            {
                logger.Warning($"SystemRebootObserved post failed: {ex.Message}");
            }
        }

        private static void EmitVersionCheckEventIfAny(
            InformationalEventPost post,
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
                    post.Emit(buildResult.Event);
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
