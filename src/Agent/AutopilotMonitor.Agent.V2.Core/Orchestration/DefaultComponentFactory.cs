#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Periodic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Production <see cref="IComponentFactory"/>. Plan §4.x M4.5.b.
    /// <para>
    /// Builds the real Collector-Hosts for the V2 agent runtime:
    /// <list type="bullet">
    ///   <item><b>EspAndHelloHost</b> — <see cref="EspAndHelloTracker"/> coordinator
    ///     (internally aggregates HelloTracker + ShellCoreTracker + ProvisioningStatusTracker
    ///     + ModernDeploymentTracker) wired via <see cref="EspAndHelloTrackerAdapter"/>.
    ///     This is the single production entry for the ESP+Hello signal surface — avoids
    ///     double emission that would happen if the sub-tracker adapters were also wired
    ///     in parallel (§4.x M4.3 tech-debt note about adapter duplication).</item>
    ///   <item><b>DesktopArrivalHost</b> — <see cref="DesktopArrivalDetector"/> + <see cref="DesktopArrivalDetectorAdapter"/>.</item>
    ///   <item><b>AadJoinHost</b> — <see cref="AadJoinWatcher"/> + <see cref="AadJoinWatcherAdapter"/>.</item>
    ///   <item><b>ImeLogHost</b> — <see cref="ImeLogTracker"/> + <see cref="ImeProcessWatcher"/>
    ///     + <see cref="ImeLogTrackerAdapter"/>.</item>
    ///   <item><b>StallProbeHost</b> — <see cref="StallProbeCollector"/> +
    ///     <see cref="StallProbeCollectorAdapter"/>. Owns its 60-s idle-check timer (the
    ///     collector itself has no timer — it's a pure probe invoked from outside).</item>
    /// </list>
    /// Optional peripheral hosts (driven by <see cref="CollectorConfiguration"/> toggles):
    /// <list type="bullet">
    ///   <item><b>PeriodicCollectorLifecycleHost</b> — owns <c>PerformanceCollector</c> (CPU /
    ///     memory / disk samples) and <c>AgentSelfMetricsCollector</c> (process CPU, memory and
    ///     HTTP traffic counters) under a single idle-timeout window (V1 parity with
    ///     <c>PeriodicCollectorManager</c>). Wires <c>AgentSelfMetricsCollector</c> into the
    ///     <see cref="NetworkMetrics"/> instance created by the Program.cs
    ///     <see cref="BackendApiClient"/>.</item>
    ///   <item><b>NetworkChangeHost</b> — owns <c>NetworkChangeDetector</c> for WiFi / SSID /
    ///     default-route / MDM-reachability transitions (V1 parity with
    ///     <c>CollectorCoordinator.StartOptionalCollectors:375-382</c>).</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>ModernDeployment Events-Bridge</b>: the ModernDeploymentTracker lives inside
    /// <see cref="EspAndHelloTracker"/>. Its diagnostic log events reach the telemetry spool
    /// via <c>onEnrollmentEvent</c>, exactly like any other EspAndHello event. No separate
    /// adapter exists (documented in <see cref="IComponentFactory"/> + verified by the
    /// <c>Modern_deployment_host_emits_event_bridge_only_no_decision_signal</c> test).
    /// </para>
    /// </summary>
    public sealed class DefaultComponentFactory : IComponentFactory
    {
        private readonly AgentConfiguration _agentConfig;
        private readonly AgentConfigResponse _remoteConfig;
        private readonly NetworkMetrics? _networkMetrics;
        private readonly string _agentVersion;
        private readonly string _stateDirectory;

        private ImeLogHost? _imeLogHost;

        /// <summary>
        /// Exposes the IME tracker's package-state list to peripheral consumers such as the
        /// <c>FinalStatusBuilder</c> in M4.6.β. Returns <c>null</c> before
        /// <see cref="CreateCollectorHosts"/> has been called (Orchestrator start order).
        /// </summary>
        public Monitoring.Enrollment.Ime.AppPackageStateList? ImePackageStates => _imeLogHost?.PackageStates;

        public DefaultComponentFactory(
            AgentConfiguration agentConfig,
            AgentConfigResponse remoteConfig,
            NetworkMetrics? networkMetrics,
            string agentVersion,
            string stateDirectory)
        {
            _agentConfig = agentConfig ?? throw new ArgumentNullException(nameof(agentConfig));
            _remoteConfig = remoteConfig ?? throw new ArgumentNullException(nameof(remoteConfig));
            _networkMetrics = networkMetrics;
            _agentVersion = string.IsNullOrEmpty(agentVersion) ? "unknown" : agentVersion;
            _stateDirectory = stateDirectory ?? throw new ArgumentNullException(nameof(stateDirectory));
        }

        public IReadOnlyList<ICollectorHost> CreateCollectorHosts(
            string sessionId,
            string tenantId,
            AgentLogger logger,
            Action<EnrollmentEvent> onEnrollmentEvent,
            IReadOnlyCollection<string> whiteGloveSealingPatternIds,
            ISignalIngressSink ingress,
            IClock clock)
        {
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("SessionId required.", nameof(sessionId));
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentException("TenantId required.", nameof(tenantId));
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (onEnrollmentEvent == null) throw new ArgumentNullException(nameof(onEnrollmentEvent));
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            if (clock == null) throw new ArgumentNullException(nameof(clock));

            var hosts = new List<ICollectorHost>();
            var collectors = _remoteConfig.Collectors ?? CollectorConfiguration.CreateDefault();

            // ----- Kernel hosts (always-on; they produce decision signals) --------------------

            hosts.Add(new EspAndHelloHost(
                sessionId: sessionId,
                tenantId: tenantId,
                logger: logger,
                ingress: ingress,
                clock: clock,
                helloWaitTimeoutSeconds: collectors.HelloWaitTimeoutSeconds,
                modernDeploymentWatcherEnabled: collectors.ModernDeploymentWatcherEnabled,
                modernDeploymentLogLevelMax: collectors.ModernDeploymentLogLevelMax,
                modernDeploymentBackfillEnabled: collectors.ModernDeploymentBackfillEnabled,
                modernDeploymentBackfillLookbackMinutes: collectors.ModernDeploymentBackfillLookbackMinutes,
                modernDeploymentHarmlessEventIds: collectors.ModernDeploymentHarmlessEventIds,
                stateDirectory: _stateDirectory));

            hosts.Add(new DesktopArrivalHost(logger, ingress, clock));

            hosts.Add(new AadJoinHost(logger, ingress, clock));

            // Dev / test — if --replay-log-dir is set, the tracker reads from the replay folder
            // with SimulationMode ON + the configured SpeedFactor instead of tailing the live
            // IME log folder. Production agents leave ReplayLogDir empty.
            var simulationMode = !string.IsNullOrEmpty(_agentConfig.ReplayLogDir);
            var imeLogFolder = simulationMode
                ? _agentConfig.ReplayLogDir
                : _agentConfig.ImeLogPathOverride;

            _imeLogHost = new ImeLogHost(
                sessionId: sessionId,
                tenantId: tenantId,
                onEnrollmentEvent: onEnrollmentEvent,
                logger: logger,
                ingress: ingress,
                clock: clock,
                imeLogPathOverride: imeLogFolder,
                imeMatchLogPath: _agentConfig.ImeMatchLogPath,
                imePatterns: _remoteConfig.ImeLogPatterns,
                stateDirectory: _stateDirectory,
                whiteGloveSealingPatternIds: whiteGloveSealingPatternIds,
                simulationMode: simulationMode,
                simulationSpeedFactor: _agentConfig.ReplaySpeedFactor);
            hosts.Add(_imeLogHost);

            if (collectors.StallProbeEnabled)
            {
                hosts.Add(new StallProbeHost(
                    sessionId: sessionId,
                    tenantId: tenantId,
                    logger: logger,
                    ingress: ingress,
                    clock: clock,
                    thresholdsMinutes: collectors.StallProbeThresholdsMinutes,
                    traceIndices: collectors.StallProbeTraceIndices,
                    sources: collectors.StallProbeSources,
                    sessionStalledAfterProbeIndex: collectors.SessionStalledAfterProbeIndex,
                    harmlessModernDeploymentEventIds: collectors.ModernDeploymentHarmlessEventIds));
            }

            // ----- Peripheral hosts (event-only; driven by remote-config toggles) --------------

            // V1 parity (PeriodicCollectorManager) — combine Performance + AgentSelfMetrics under
            // a single host that stops both after CollectorIdleTimeoutMinutes of no real enrollment
            // activity and restarts them on the next real event. Without the idle timeout the two
            // collectors run for the agent's entire lifetime (up to AgentMaxLifetime / 6 h),
            // which drains battery + wastes bandwidth on dormant sessions.
            if (collectors.EnablePerformanceCollector || (collectors.EnableAgentSelfMetrics && _networkMetrics != null))
            {
                hosts.Add(new PeriodicCollectorLifecycleHost(
                    sessionId: sessionId,
                    tenantId: tenantId,
                    ingress: ingress,
                    clock: clock,
                    logger: logger,
                    performanceEnabled: collectors.EnablePerformanceCollector,
                    performanceIntervalSeconds: collectors.PerformanceIntervalSeconds,
                    selfMetricsEnabled: collectors.EnableAgentSelfMetrics && _networkMetrics != null,
                    selfMetricsIntervalSeconds: collectors.AgentSelfMetricsIntervalSeconds,
                    idleTimeoutMinutes: collectors.CollectorIdleTimeoutMinutes,
                    networkMetrics: _networkMetrics,
                    agentVersion: _agentVersion));
            }

            // V1 parity (CollectorCoordinator.StartOptionalCollectors:375-382) — wire the
            // NetworkChangeDetector. It captures WiFi SSID / default route / IPv4 / reachability
            // changes and emits `network_change` events. Events are already debounced internally
            // (5s); no separate remote-config toggle in V1 either.
            hosts.Add(new NetworkChangeHost(
                sessionId: sessionId,
                tenantId: tenantId,
                ingress: ingress,
                clock: clock,
                logger: logger,
                apiBaseUrl: _agentConfig.ApiBaseUrl));

            // M4.6.γ — Delivery-Optimization telemetry. Dormant-by-default: only polls when the
            // IME log tracker reports an app entering Downloading/Installing (see AppStateChanged
            // chain below). Needs the IME tracker's PackageStates + OnDoTelemetryReceived hook.
            if (collectors.EnableDeliveryOptimizationCollector && _imeLogHost != null)
            {
                var doHost = new DeliveryOptimizationHost(
                    sessionId: sessionId,
                    tenantId: tenantId,
                    ingress: ingress,
                    clock: clock,
                    logger: logger,
                    intervalSeconds: collectors.DeliveryOptimizationIntervalSeconds,
                    imeHost: _imeLogHost);
                hosts.Add(doHost);
            }

            // M4.6.δ — Gather-rules runtime executor. Runs the backend-defined rules whose
            // Trigger is "startup" once the agent is up; signal / event / periodic triggers
            // remain supported inside the executor itself.
            if (_remoteConfig.GatherRules != null && _remoteConfig.GatherRules.Count > 0)
            {
                hosts.Add(new GatherRuleExecutorHost(
                    sessionId: sessionId,
                    tenantId: tenantId,
                    ingress: ingress,
                    clock: clock,
                    logger: logger,
                    rules: _remoteConfig.GatherRules,
                    imeLogPathOverride: _agentConfig.ImeLogPathOverride,
                    unrestrictedMode: _agentConfig.UnrestrictedMode));
            }

            return hosts;
        }

        // =====================================================================================
        // Hosts
        // =====================================================================================

        private sealed class GatherRuleExecutorHost : ICollectorHost
        {
            public string Name => "GatherRuleExecutor";

            private readonly Monitoring.Telemetry.Gather.GatherRuleExecutor _executor;
            private readonly System.Collections.Generic.List<AutopilotMonitor.Shared.Models.GatherRule> _rules;
            private readonly AgentLogger _logger;
            private readonly bool _unrestrictedMode;
            private int _disposed;

            public GatherRuleExecutorHost(
                string sessionId,
                string tenantId,
                ISignalIngressSink ingress,
                IClock clock,
                AgentLogger logger,
                System.Collections.Generic.List<AutopilotMonitor.Shared.Models.GatherRule> rules,
                string? imeLogPathOverride,
                bool unrestrictedMode = false)
            {
                if (ingress == null) throw new ArgumentNullException(nameof(ingress));
                if (clock == null) throw new ArgumentNullException(nameof(clock));
                _logger = logger;
                _rules = rules ?? new System.Collections.Generic.List<AutopilotMonitor.Shared.Models.GatherRule>();
                _unrestrictedMode = unrestrictedMode;

                // Single-rail routing (plan §5.6): the gather executor and its collectors keep
                // their internal Action<EnrollmentEvent> signature because (a) they have no
                // interface contract and (b) the standalone --run-gather-rules CLI mode still
                // needs to collect raw EnrollmentEvents in-memory for the direct
                // BackendApiClient.IngestEventsAsync upload (plan §9 orthogonal world). In
                // session mode we wrap post.Emit so every session-mode gather event still
                // flows through the InformationalEvent ingress pipe before hitting the
                // telemetry spool — Rail-A semantics for ordering / replay determinism.
                var post = new InformationalEventPost(ingress, clock);
                _executor = new Monitoring.Telemetry.Gather.GatherRuleExecutor(
                    sessionId, tenantId, evt => post.Emit(evt), logger, imeLogPathOverride);
            }

            public void Start()
            {
                // V1 parity (CollectorCoordinator.StartGatherRuleExecutor) — propagate the
                // tenant-controlled UnrestrictedMode BEFORE UpdateRules so any startup-trigger
                // rule sees the elevated policy when AllowList checks would otherwise reject it.
                _executor.UnrestrictedMode = _unrestrictedMode;
                _executor.UpdateRules(_rules);
                _logger.Info(
                    $"GatherRuleExecutorHost: started with {_rules.Count} rule(s), unrestrictedMode={_unrestrictedMode}.");
            }

            public void Stop()
            {
                // GatherRuleExecutor is IDisposable; no explicit Stop. Rely on Dispose.
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
                try { _executor.Dispose(); } catch { }
            }
        }

        private sealed class EspAndHelloHost : ICollectorHost
        {
            public string Name => "EspAndHelloTracker";

            private readonly EspAndHelloTracker _tracker;
            private readonly EspAndHelloTrackerAdapter _adapter;
            private int _disposed;

            public EspAndHelloHost(
                string sessionId,
                string tenantId,
                AgentLogger logger,
                ISignalIngressSink ingress,
                IClock clock,
                int helloWaitTimeoutSeconds,
                bool modernDeploymentWatcherEnabled,
                int modernDeploymentLogLevelMax,
                bool modernDeploymentBackfillEnabled,
                int modernDeploymentBackfillLookbackMinutes,
                int[]? modernDeploymentHarmlessEventIds,
                string stateDirectory)
            {
                if (ingress == null) throw new ArgumentNullException(nameof(ingress));
                if (clock == null) throw new ArgumentNullException(nameof(clock));
                var post = new InformationalEventPost(ingress, clock);
                _tracker = new EspAndHelloTracker(
                    sessionId: sessionId,
                    tenantId: tenantId,
                    post: post,
                    logger: logger,
                    helloWaitTimeoutSeconds: helloWaitTimeoutSeconds,
                    modernDeploymentWatcherEnabled: modernDeploymentWatcherEnabled,
                    modernDeploymentLogLevelMax: modernDeploymentLogLevelMax,
                    modernDeploymentBackfillEnabled: modernDeploymentBackfillEnabled,
                    modernDeploymentBackfillLookbackMinutes: modernDeploymentBackfillLookbackMinutes,
                    stateDirectory: stateDirectory,
                    modernDeploymentHarmlessEventIds: modernDeploymentHarmlessEventIds);

                _adapter = new EspAndHelloTrackerAdapter(_tracker, ingress, clock);
            }

            public void Start() => _tracker.Start();
            public void Stop() => _tracker.Stop();

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
                try { _adapter.Dispose(); } catch { }
                try { _tracker.Dispose(); } catch { }
            }
        }

        private sealed class DesktopArrivalHost : ICollectorHost
        {
            public string Name => "DesktopArrivalDetector";

            private readonly DesktopArrivalDetector _detector;
            private readonly DesktopArrivalDetectorAdapter _adapter;
            private int _disposed;

            public DesktopArrivalHost(AgentLogger logger, ISignalIngressSink ingress, IClock clock)
            {
                _detector = new DesktopArrivalDetector(logger);
                _adapter = new DesktopArrivalDetectorAdapter(_detector, ingress, clock);
            }

            public void Start() => _detector.Start();
            public void Stop() => _detector.Stop();

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
                try { _adapter.Dispose(); } catch { }
                try { _detector.Dispose(); } catch { }
            }
        }

        private sealed class AadJoinHost : ICollectorHost
        {
            public string Name => "AadJoinWatcher";

            private readonly AadJoinWatcher _watcher;
            private readonly AadJoinWatcherAdapter _adapter;
            private int _disposed;

            public AadJoinHost(AgentLogger logger, ISignalIngressSink ingress, IClock clock)
            {
                _watcher = new AadJoinWatcher(logger);
                _adapter = new AadJoinWatcherAdapter(_watcher, ingress, clock);
            }

            public void Start() => _watcher.Start();
            public void Stop() => _watcher.Stop();

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
                try { _adapter.Dispose(); } catch { }
                try { _watcher.Dispose(); } catch { }
            }
        }

        private sealed class ImeLogHost : ICollectorHost
        {
            public string Name => "ImeLogTracker";

            private const string DefaultImeLogFolder = @"%ProgramData%\Microsoft\IntuneManagementExtension\Logs";

            private readonly ImeLogTracker _tracker;
            private readonly ImeProcessWatcher _processWatcher;
            private readonly ImeLogTrackerAdapter _adapter;
            private readonly AgentLogger _logger;
            private int _disposed;

            /// <summary>
            /// Exposes the tracker's live package-state list for peripheral consumers
            /// (M4.6.β <c>FinalStatusBuilder</c>, M4.6.γ <c>DeliveryOptimizationHost</c>).
            /// </summary>
            public Monitoring.Enrollment.Ime.AppPackageStateList PackageStates => _tracker.PackageStates;

            /// <summary>
            /// Reference to the wrapped IME tracker for co-collector wiring. Used by
            /// <c>DeliveryOptimizationHost</c> to set <c>OnDoTelemetryReceived</c> and to chain
            /// <c>OnAppStateChanged</c> for dormant/wake-up transitions.
            /// </summary>
            internal Monitoring.Enrollment.Ime.ImeLogTracker Tracker => _tracker;

            /// <summary>
            /// Exposes the tracker's simulation flag so the Dev / Test CLI flag
            /// <c>--replay-log-dir</c> is testable without poking through reflection.
            /// </summary>
            internal bool IsSimulationMode => _tracker.SimulationMode;

            public ImeLogHost(
                string sessionId,
                string tenantId,
                Action<EnrollmentEvent> onEnrollmentEvent,
                AgentLogger logger,
                ISignalIngressSink ingress,
                IClock clock,
                string? imeLogPathOverride,
                string? imeMatchLogPath,
                List<ImeLogPattern>? imePatterns,
                string stateDirectory,
                IReadOnlyCollection<string>? whiteGloveSealingPatternIds,
                bool simulationMode = false,
                double simulationSpeedFactor = 50)
            {
                _logger = logger;
                var logFolder = string.IsNullOrEmpty(imeLogPathOverride) ? DefaultImeLogFolder : imeLogPathOverride!;
                var expandedMatchLogPath = string.IsNullOrEmpty(imeMatchLogPath)
                    ? null
                    : Environment.ExpandEnvironmentVariables(imeMatchLogPath);
                var patterns = imePatterns ?? new List<ImeLogPattern>();

                _tracker = new ImeLogTracker(
                    logFolder: logFolder,
                    patterns: patterns,
                    logger: logger,
                    matchLogPath: expandedMatchLogPath,
                    stateDirectory: stateDirectory);

                if (simulationMode)
                {
                    _tracker.SimulationMode = true;
                    _tracker.SpeedFactor = simulationSpeedFactor;
                    logger.Info($"ImeLogHost: SimulationMode ENABLED (speedFactor={simulationSpeedFactor}, path={logFolder})");
                }

                _adapter = new ImeLogTrackerAdapter(_tracker, ingress, clock, whiteGloveSealingPatternIds);

                var processWatcherPost = new InformationalEventPost(ingress, clock);
                _processWatcher = new ImeProcessWatcher(sessionId, tenantId, processWatcherPost, logger);
            }

            public void Start()
            {
                _tracker.Start();
                _processWatcher.Start();
            }

            public void Stop()
            {
                try { _processWatcher.Dispose(); }
                catch (Exception ex) { _logger.Warning($"ImeLogHost: processWatcher dispose failed: {ex.Message}"); }
                try { _tracker.Stop(); }
                catch (Exception ex) { _logger.Warning($"ImeLogHost: tracker stop failed: {ex.Message}"); }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
                try { _adapter.Dispose(); } catch { }
                try { _processWatcher.Dispose(); } catch { }
                try { _tracker.Stop(); } catch { }
            }
        }

        private sealed class StallProbeHost : ICollectorHost
        {
            public string Name => "StallProbeCollector";

            private static readonly TimeSpan IdleTickInterval = TimeSpan.FromSeconds(60);

            private readonly StallProbeCollector _collector;
            private readonly StallProbeCollectorAdapter _adapter;
            private readonly AgentLogger _logger;
            private Timer? _tickTimer;
            private readonly DateTime _startedAtUtc;
            private int _disposed;

            public StallProbeHost(
                string sessionId,
                string tenantId,
                AgentLogger logger,
                ISignalIngressSink ingress,
                IClock clock,
                int[]? thresholdsMinutes,
                int[]? traceIndices,
                string[]? sources,
                int sessionStalledAfterProbeIndex,
                int[]? harmlessModernDeploymentEventIds)
            {
                if (ingress == null) throw new ArgumentNullException(nameof(ingress));
                if (clock == null) throw new ArgumentNullException(nameof(clock));
                _logger = logger;
                var post = new InformationalEventPost(ingress, clock);
                _collector = new StallProbeCollector(
                    sessionId: sessionId,
                    tenantId: tenantId,
                    post: post,
                    logger: logger,
                    thresholdsMinutes: thresholdsMinutes ?? new[] { 2, 15, 30, 60, 180 },
                    traceIndices: traceIndices ?? new[] { 2 },
                    sources: sources ?? new[] { "provisioning_registry", "diagnostics_registry", "eventlog", "appworkload_log" },
                    sessionStalledAfterProbeIndex: sessionStalledAfterProbeIndex,
                    harmlessModernDeploymentEventIds: harmlessModernDeploymentEventIds);

                _adapter = new StallProbeCollectorAdapter(_collector, ingress, clock);
                _startedAtUtc = DateTime.UtcNow;
            }

            public void Start()
            {
                // The collector has no timer of its own — we drive it with a 60-s idle tick.
                _tickTimer = new Timer(
                    _ => SafeTick(),
                    state: null,
                    dueTime: IdleTickInterval,
                    period: IdleTickInterval);
                _logger.Info($"StallProbeHost: started (tick every {IdleTickInterval.TotalSeconds}s).");
            }

            public void Stop()
            {
                try
                {
                    _tickTimer?.Dispose();
                    _tickTimer = null;
                }
                catch (Exception ex) { _logger.Warning($"StallProbeHost: timer dispose failed: {ex.Message}"); }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
                Stop();
                try { _adapter.Dispose(); } catch { }
            }

            private void SafeTick()
            {
                try
                {
                    var idleMinutes = (DateTime.UtcNow - _startedAtUtc).TotalMinutes;
                    _collector.CheckAndRunProbes(idleMinutes);
                }
                catch (Exception ex)
                {
                    _logger.Error("StallProbeHost: tick failed.", ex);
                }
            }
        }

        private sealed class DeliveryOptimizationHost : ICollectorHost
        {
            public string Name => "DeliveryOptimizationCollector";

            private readonly DeliveryOptimizationCollector _collector;
            private readonly ImeLogHost _imeHost;
            private readonly AgentLogger _logger;
            private Action<Monitoring.Enrollment.Ime.AppPackageState, Monitoring.Enrollment.Ime.AppInstallationState, Monitoring.Enrollment.Ime.AppInstallationState>? _prevStateChanged;
            private int _disposed;

            public DeliveryOptimizationHost(
                string sessionId,
                string tenantId,
                ISignalIngressSink ingress,
                IClock clock,
                AgentLogger logger,
                int intervalSeconds,
                ImeLogHost imeHost)
            {
                if (ingress == null) throw new ArgumentNullException(nameof(ingress));
                if (clock == null) throw new ArgumentNullException(nameof(clock));
                _imeHost = imeHost;
                _logger = logger;

                var post = new InformationalEventPost(ingress, clock);
                _collector = new DeliveryOptimizationCollector(
                    sessionId: sessionId,
                    tenantId: tenantId,
                    post: post,
                    logger: logger,
                    intervalSeconds: intervalSeconds,
                    getPackageStates: () => imeHost.PackageStates,
                    onDoTelemetryReceived: pkg =>
                    {
                        try { imeHost.Tracker.OnDoTelemetryReceived?.Invoke(pkg); }
                        catch (Exception ex) { logger.Warning($"DeliveryOptimizationHost: OnDoTelemetryReceived invocation threw: {ex.Message}"); }
                    },
                    logDirectory: Environment.ExpandEnvironmentVariables(Shared.Constants.LogDirectory));
            }

            private Action<Monitoring.Enrollment.Ime.AppPackageState, Monitoring.Enrollment.Ime.AppInstallationState, Monitoring.Enrollment.Ime.AppInstallationState>? _chainedHandler;

            public void Start()
            {
                // Chain ourselves into the tracker's OnAppStateChanged so the DO collector wakes
                // up on the first Downloading/Installing transition (Legacy parity). We preserve
                // any existing handler — the IME adapter's own listener must keep running.
                _prevStateChanged = _imeHost.Tracker.OnAppStateChanged;
                _chainedHandler = (pkg, oldState, newState) =>
                {
                    try { _prevStateChanged?.Invoke(pkg, oldState, newState); }
                    catch (Exception ex) { _logger.Warning($"DeliveryOptimizationHost: previous OnAppStateChanged handler threw: {ex.Message}"); }

                    if (newState >= Monitoring.Enrollment.Ime.AppInstallationState.Downloading &&
                        newState <= Monitoring.Enrollment.Ime.AppInstallationState.Installing)
                    {
                        try { _collector.WakeUp(); }
                        catch (Exception ex) { _logger.Warning($"DeliveryOptimizationHost: WakeUp threw: {ex.Message}"); }
                    }
                };
                _imeHost.Tracker.OnAppStateChanged = _chainedHandler;

                _collector.Start();
                _logger.Info($"DeliveryOptimizationHost: started dormant (interval={_collector}, wakes on Downloading/Installing).");
            }

            public void Stop()
            {
                // Restore the previous handler only if we're still the current one — otherwise
                // someone else replaced us and owns the slot now. The V1 bug here compared the
                // handler slot against itself (always true) which silently overwrote any newer
                // handler that had since taken over; the captured `_chainedHandler` reference
                // lets us make the check actually meaningful.
                if (_chainedHandler != null
                    && object.ReferenceEquals(_imeHost.Tracker.OnAppStateChanged, _chainedHandler))
                {
                    _imeHost.Tracker.OnAppStateChanged = _prevStateChanged;
                }
                _collector.Stop();
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
                try { _collector.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// V1 parity (<c>PeriodicCollectorManager</c>). Owns the <see cref="PerformanceCollector"/>
        /// and <see cref="AgentSelfMetricsCollector"/> and enforces the
        /// <c>CollectorIdleTimeoutMinutes</c> window: both stop after N minutes of no real event
        /// activity and restart on the next real (non-periodic) event. Without this the two
        /// collectors ran forever in V2 and filled the telemetry spool on dormant sessions.
        /// <para>
        /// Single-rail refactor (plan §5.4): both collectors and the idle-stopped events emit
        /// through a shared <see cref="InformationalEventPost"/> constructed over an
        /// <see cref="IdleActivityObservingIngressSink"/> wrapper. The wrapper peeks every posted
        /// signal's <c>eventType</c> payload and bumps <c>_lastRealEventTimeUtc</c> unless the
        /// event belongs to a well-known periodic type (<c>performance_snapshot</c>,
        /// <c>agent_metrics_snapshot</c>, <c>performance_collector_stopped</c>,
        /// <c>agent_metrics_collector_stopped</c>, <c>stall_probe_*</c>, <c>session_stalled</c>).
        /// Events emitted by the managed collectors themselves therefore never reset their own
        /// idle window.
        /// </para>
        /// </summary>
        private sealed class PeriodicCollectorLifecycleHost : ICollectorHost
        {
            public string Name => "PeriodicCollectorLifecycleHost";

            private static readonly TimeSpan IdleTickInterval = TimeSpan.FromSeconds(60);
            private static readonly HashSet<string> PeriodicEventTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "performance_snapshot",
                "agent_metrics_snapshot",
                "performance_collector_stopped",
                "agent_metrics_collector_stopped",
                "stall_probe_check",
                "stall_probe_result",
                "session_stalled",
            };

            private readonly string _sessionId;
            private readonly string _tenantId;
            private readonly InformationalEventPost _post;
            private readonly AgentLogger _logger;
            private readonly bool _perfEnabled;
            private readonly int _perfIntervalSeconds;
            private readonly bool _selfMetricsEnabled;
            private readonly int _selfMetricsIntervalSeconds;
            private readonly int _idleTimeoutMinutes;
            private readonly NetworkMetrics? _networkMetrics;
            private readonly string _agentVersion;

            private readonly object _sync = new object();
            private PerformanceCollector? _performanceCollector;
            private AgentSelfMetricsCollector? _selfMetricsCollector;
            private Timer? _idleTimer;
            private DateTime _lastRealEventTimeUtc;
            private bool _idleStopped;
            private int _disposed;

            public PeriodicCollectorLifecycleHost(
                string sessionId,
                string tenantId,
                ISignalIngressSink ingress,
                IClock clock,
                AgentLogger logger,
                bool performanceEnabled,
                int performanceIntervalSeconds,
                bool selfMetricsEnabled,
                int selfMetricsIntervalSeconds,
                int idleTimeoutMinutes,
                NetworkMetrics? networkMetrics,
                string agentVersion)
            {
                if (ingress == null) throw new ArgumentNullException(nameof(ingress));
                if (clock == null) throw new ArgumentNullException(nameof(clock));
                _sessionId = sessionId;
                _tenantId = tenantId;
                _logger = logger;
                _perfEnabled = performanceEnabled;
                _perfIntervalSeconds = performanceIntervalSeconds;
                _selfMetricsEnabled = selfMetricsEnabled;
                _selfMetricsIntervalSeconds = selfMetricsIntervalSeconds;
                _idleTimeoutMinutes = idleTimeoutMinutes;
                _networkMetrics = networkMetrics;
                _agentVersion = agentVersion;
                _lastRealEventTimeUtc = DateTime.UtcNow;

                // Wrap the ingress so every posted signal triggers the idle-activity observer.
                // Collector emissions + EmitIdleStopped all flow through the same post, so the
                // activity check runs at the same point it did for the legacy Action<EnrollmentEvent>
                // wrapper — only now it sits at the ingress layer instead of the pre-emitter sink.
                var observingIngress = new IdleActivityObservingIngressSink(ingress, ObserveEventType);
                _post = new InformationalEventPost(observingIngress, clock);
            }

            private void ObserveEventType(string? eventType)
            {
                if (string.IsNullOrEmpty(eventType)) return;
                if (PeriodicEventTypes.Contains(eventType!)) return;
                OnRealEvent();
            }

            public void Start()
            {
                lock (_sync)
                {
                    StartCollectorsInternal();
                    if (_idleTimeoutMinutes > 0)
                    {
                        _idleTimer = new Timer(_ => IdleCheckTick(), state: null,
                            dueTime: IdleTickInterval, period: IdleTickInterval);
                        _logger.Info($"PeriodicCollectorLifecycleHost: started (idle timeout={_idleTimeoutMinutes}min).");
                    }
                    else
                    {
                        _logger.Info("PeriodicCollectorLifecycleHost: started (idle timeout disabled).");
                    }
                }
            }

            public void Stop()
            {
                lock (_sync)
                {
                    try { _idleTimer?.Dispose(); } catch { }
                    _idleTimer = null;
                    StopCollectorsInternal();
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
                Stop();
            }

            private void StartCollectorsInternal()
            {
                if (_perfEnabled && _performanceCollector == null)
                {
                    _performanceCollector = new PerformanceCollector(
                        _sessionId, _tenantId, _post, _logger, _perfIntervalSeconds);
                    _performanceCollector.Start();
                }
                if (_selfMetricsEnabled && _selfMetricsCollector == null && _networkMetrics != null)
                {
                    _selfMetricsCollector = new AgentSelfMetricsCollector(
                        _sessionId, _tenantId, _post, _networkMetrics, _logger, _agentVersion, _selfMetricsIntervalSeconds);
                    _selfMetricsCollector.Start();
                }
                _idleStopped = false;
            }

            private void StopCollectorsInternal()
            {
                try { _performanceCollector?.Stop(); } catch { }
                try { _performanceCollector?.Dispose(); } catch { }
                _performanceCollector = null;

                try { _selfMetricsCollector?.Stop(); } catch { }
                try { _selfMetricsCollector?.Dispose(); } catch { }
                _selfMetricsCollector = null;
            }

            private void OnRealEvent()
            {
                lock (_sync)
                {
                    _lastRealEventTimeUtc = DateTime.UtcNow;
                    if (_idleStopped)
                    {
                        _logger.Info("PeriodicCollectorLifecycleHost: real event detected — restarting periodic collectors.");
                        StartCollectorsInternal();
                    }
                }
            }

            private void IdleCheckTick()
            {
                try
                {
                    lock (_sync)
                    {
                        if (_idleStopped) return;
                        if (_idleTimeoutMinutes <= 0) return;

                        var idleMinutes = (DateTime.UtcNow - _lastRealEventTimeUtc).TotalMinutes;
                        if (idleMinutes < _idleTimeoutMinutes) return;

                        _logger.Info($"PeriodicCollectorLifecycleHost: idle for {idleMinutes:F0}min (limit={_idleTimeoutMinutes}) — stopping collectors.");

                        var hadPerformance = _performanceCollector != null;
                        var hadSelfMetrics = _selfMetricsCollector != null;
                        StopCollectorsInternal();
                        _idleStopped = true;

                        if (hadPerformance)
                            EmitIdleStopped("performance_collector_stopped", idleMinutes);
                        if (hadSelfMetrics)
                            EmitIdleStopped("agent_metrics_collector_stopped", idleMinutes);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning($"PeriodicCollectorLifecycleHost: idle-check tick threw: {ex.Message}");
                }
            }

            private void EmitIdleStopped(string eventType, double idleMinutes)
            {
                try
                {
                    _post.Emit(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        EventType = eventType,
                        Severity = EventSeverity.Info,
                        Source = "PeriodicCollectorLifecycleHost",
                        Phase = EnrollmentPhase.Unknown,
                        Message = $"{eventType} after {idleMinutes:F0}min idle (no real enrollment activity).",
                        Data = new Dictionary<string, object>
                        {
                            { "reason", "idle_timeout" },
                            { "idleTimeoutMinutes", _idleTimeoutMinutes },
                            { "idleMinutes", Math.Round(idleMinutes, 1) },
                        },
                    });
                }
                catch (Exception ex) { _logger.Debug($"PeriodicCollectorLifecycleHost: emit '{eventType}' threw: {ex.Message}"); }
            }

            /// <summary>
            /// Ingress sink wrapper that peeks every posted signal's <c>eventType</c> payload for
            /// idle-activity observation, then forwards to the real ingress unchanged. Plan §5.4.
            /// </summary>
            private sealed class IdleActivityObservingIngressSink : ISignalIngressSink
            {
                private readonly ISignalIngressSink _inner;
                private readonly Action<string?> _observeEventType;

                public IdleActivityObservingIngressSink(ISignalIngressSink inner, Action<string?> observeEventType)
                {
                    _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                    _observeEventType = observeEventType ?? throw new ArgumentNullException(nameof(observeEventType));
                }

                public void Post(
                    DecisionSignalKind kind,
                    DateTime occurredAtUtc,
                    string sourceOrigin,
                    Evidence evidence,
                    IReadOnlyDictionary<string, string>? payload = null,
                    int kindSchemaVersion = 1)
                {
                    string? eventType = null;
                    if (payload != null && payload.TryGetValue(SignalPayloadKeys.EventType, out var value))
                    {
                        eventType = value;
                    }
                    try { _observeEventType(eventType); }
                    catch { /* observer exceptions must never break the ingress pipe */ }
                    _inner.Post(kind, occurredAtUtc, sourceOrigin, evidence, payload, kindSchemaVersion);
                }
            }
        }

        /// <summary>
        /// V1 parity (<c>CollectorCoordinator.StartOptionalCollectors:375-382</c>) — owns the
        /// lifecycle of <see cref="NetworkChangeDetector"/>. Without this host the detector class
        /// existed in V2 but was never instantiated, so WiFi / SSID / connectivity transitions
        /// were invisible to the backend.
        /// </summary>
        private sealed class NetworkChangeHost : ICollectorHost
        {
            public string Name => "NetworkChangeDetector";

            private readonly NetworkChangeDetector _detector;
            private readonly AgentLogger _logger;
            private int _disposed;

            public NetworkChangeHost(
                string sessionId,
                string tenantId,
                ISignalIngressSink ingress,
                IClock clock,
                AgentLogger logger,
                string? apiBaseUrl)
            {
                if (ingress == null) throw new ArgumentNullException(nameof(ingress));
                if (clock == null) throw new ArgumentNullException(nameof(clock));
                _logger = logger;
                var post = new InformationalEventPost(ingress, clock);
                _detector = new NetworkChangeDetector(sessionId, tenantId, post, logger, apiBaseUrl);
            }

            public void Start()
            {
                try { _detector.Start(); }
                catch (Exception ex) { _logger.Warning($"NetworkChangeHost: Start failed: {ex.Message}"); }
            }

            public void Stop()
            {
                try { _detector.Stop(); }
                catch (Exception ex) { _logger.Warning($"NetworkChangeHost: Stop failed: {ex.Message}"); }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
                try { _detector.Dispose(); } catch { }
            }
        }
    }
}
