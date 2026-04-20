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
    ///   <item><b>PerformanceHost</b> — CPU/memory/disk samples.</item>
    ///   <item><b>AgentSelfMetricsHost</b> — process CPU, memory and HTTP traffic counters.
    ///     Wires into the <see cref="NetworkMetrics"/> instance created by the Program.cs
    ///     <see cref="BackendApiClient"/> (the V2 <see cref="Transport.Telemetry.BackendTelemetryUploader"/>
    ///     has no metrics — accepted M4.5.b tech-debt).</item>
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
                onEnrollmentEvent: onEnrollmentEvent,
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

            _imeLogHost = new ImeLogHost(
                sessionId: sessionId,
                tenantId: tenantId,
                onEnrollmentEvent: onEnrollmentEvent,
                logger: logger,
                ingress: ingress,
                clock: clock,
                imeLogPathOverride: _agentConfig.ImeLogPathOverride,
                imeMatchLogPath: _agentConfig.ImeMatchLogPath,
                imePatterns: _remoteConfig.ImeLogPatterns,
                stateDirectory: _stateDirectory,
                whiteGloveSealingPatternIds: whiteGloveSealingPatternIds);
            hosts.Add(_imeLogHost);

            if (collectors.StallProbeEnabled)
            {
                hosts.Add(new StallProbeHost(
                    sessionId: sessionId,
                    tenantId: tenantId,
                    onEnrollmentEvent: onEnrollmentEvent,
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

            if (collectors.EnablePerformanceCollector)
            {
                hosts.Add(new PerformanceHost(sessionId, tenantId, onEnrollmentEvent, logger,
                    collectors.PerformanceIntervalSeconds));
            }

            if (collectors.EnableAgentSelfMetrics && _networkMetrics != null)
            {
                hosts.Add(new AgentSelfMetricsHost(sessionId, tenantId, onEnrollmentEvent, logger,
                    _networkMetrics, _agentVersion, collectors.AgentSelfMetricsIntervalSeconds));
            }

            // M4.6.γ — Delivery-Optimization telemetry. Dormant-by-default: only polls when the
            // IME log tracker reports an app entering Downloading/Installing (see AppStateChanged
            // chain below). Needs the IME tracker's PackageStates + OnDoTelemetryReceived hook.
            if (collectors.EnableDeliveryOptimizationCollector && _imeLogHost != null)
            {
                var doHost = new DeliveryOptimizationHost(
                    sessionId: sessionId,
                    tenantId: tenantId,
                    onEnrollmentEvent: onEnrollmentEvent,
                    logger: logger,
                    intervalSeconds: collectors.DeliveryOptimizationIntervalSeconds,
                    imeHost: _imeLogHost);
                hosts.Add(doHost);
            }

            return hosts;
        }

        // =====================================================================================
        // Hosts
        // =====================================================================================

        private sealed class EspAndHelloHost : ICollectorHost
        {
            public string Name => "EspAndHelloTracker";

            private readonly EspAndHelloTracker _tracker;
            private readonly EspAndHelloTrackerAdapter _adapter;
            private int _disposed;

            public EspAndHelloHost(
                string sessionId,
                string tenantId,
                Action<EnrollmentEvent> onEnrollmentEvent,
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
                _tracker = new EspAndHelloTracker(
                    sessionId: sessionId,
                    tenantId: tenantId,
                    onEventCollected: onEnrollmentEvent,
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
                IReadOnlyCollection<string>? whiteGloveSealingPatternIds)
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

                _adapter = new ImeLogTrackerAdapter(_tracker, ingress, clock, whiteGloveSealingPatternIds);

                _processWatcher = new ImeProcessWatcher(sessionId, tenantId, onEnrollmentEvent, logger);
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
                Action<EnrollmentEvent> onEnrollmentEvent,
                AgentLogger logger,
                ISignalIngressSink ingress,
                IClock clock,
                int[]? thresholdsMinutes,
                int[]? traceIndices,
                string[]? sources,
                int sessionStalledAfterProbeIndex,
                int[]? harmlessModernDeploymentEventIds)
            {
                _logger = logger;
                _collector = new StallProbeCollector(
                    sessionId: sessionId,
                    tenantId: tenantId,
                    onEventCollected: onEnrollmentEvent,
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

        private sealed class PerformanceHost : ICollectorHost
        {
            public string Name => "PerformanceCollector";

            private readonly PerformanceCollector _collector;
            private int _disposed;

            public PerformanceHost(
                string sessionId,
                string tenantId,
                Action<EnrollmentEvent> onEnrollmentEvent,
                AgentLogger logger,
                int intervalSeconds)
            {
                _collector = new PerformanceCollector(sessionId, tenantId, onEnrollmentEvent, logger, intervalSeconds);
            }

            public void Start() => _collector.Start();
            public void Stop() => _collector.Stop();

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
                try { _collector.Dispose(); } catch { }
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
                Action<EnrollmentEvent> onEnrollmentEvent,
                AgentLogger logger,
                int intervalSeconds,
                ImeLogHost imeHost)
            {
                _imeHost = imeHost;
                _logger = logger;

                _collector = new DeliveryOptimizationCollector(
                    sessionId: sessionId,
                    tenantId: tenantId,
                    emitEvent: onEnrollmentEvent,
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

            public void Start()
            {
                // Chain ourselves into the tracker's OnAppStateChanged so the DO collector wakes
                // up on the first Downloading/Installing transition (Legacy parity). We preserve
                // any existing handler — the IME adapter's own listener must keep running.
                _prevStateChanged = _imeHost.Tracker.OnAppStateChanged;
                _imeHost.Tracker.OnAppStateChanged = (pkg, oldState, newState) =>
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

                _collector.Start();
                _logger.Info($"DeliveryOptimizationHost: started dormant (interval={_collector}, wakes on Downloading/Installing).");
            }

            public void Stop()
            {
                // Restore the previous handler only if we're still the current one — otherwise
                // someone else replaced us and owns the slot now.
                if (object.ReferenceEquals(_imeHost.Tracker.OnAppStateChanged, _imeHost.Tracker.OnAppStateChanged))
                    _imeHost.Tracker.OnAppStateChanged = _prevStateChanged;
                _collector.Stop();
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
                try { _collector.Dispose(); } catch { }
            }
        }

        private sealed class AgentSelfMetricsHost : ICollectorHost
        {
            public string Name => "AgentSelfMetricsCollector";

            private readonly AgentSelfMetricsCollector _collector;
            private int _disposed;

            public AgentSelfMetricsHost(
                string sessionId,
                string tenantId,
                Action<EnrollmentEvent> onEnrollmentEvent,
                AgentLogger logger,
                NetworkMetrics networkMetrics,
                string agentVersion,
                int intervalSeconds)
            {
                _collector = new AgentSelfMetricsCollector(
                    sessionId, tenantId, onEnrollmentEvent, networkMetrics, logger, agentVersion, intervalSeconds);
            }

            public void Start() => _collector.Start();
            public void Stop() => _collector.Stop();

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
                try { _collector.Dispose(); } catch { }
            }
        }
    }
}
