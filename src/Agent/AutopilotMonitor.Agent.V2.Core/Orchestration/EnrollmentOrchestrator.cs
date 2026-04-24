#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Telemetry.Events;
using AutopilotMonitor.Agent.V2.Core.Telemetry.Signals;
using AutopilotMonitor.Agent.V2.Core.Telemetry.Transitions;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Top-Level-Lifecycle-Owner der V2-Agent-Runtime. Plan §2.1a / §2.7 / §4.x M4.4.5.
    /// <para>
    /// <b>Scope M4.4.5.b</b>: verdrahtet die Kern-Pipeline — Persistenz (SignalLog + Journal +
    /// Snapshot), Telemetry-Transport (Spool + Uploader), Event-Emitter-Kette,
    /// DeadlineScheduler → Ingress-Bridge, ClassifierRegistry, EffectRunner,
    /// DecisionStepProcessor, SignalIngress. Sub-c erweitert um Collectors + SignalAdapters.
    /// </para>
    /// <para>
    /// <b>Startup-Reihenfolge</b> (ctor → <see cref="Start"/>):
    /// <list type="number">
    ///   <item>Persistenz-Writer instanziieren (Sofort-Flush aktiv ab hier)</item>
    ///   <item><c>snapshot.Load()</c> → <c>initialState</c>; fallback <see cref="DecisionState.CreateInitial"/></item>
    ///   <item>TelemetrySpool + BackendUploader + TelemetryUploadOrchestrator</item>
    ///   <item>EventSequenceCounter → TelemetryEventEmitter → EventTimelineEmitter + BackPressureObserver</item>
    ///   <item>DeadlineScheduler + ClassifierRegistry</item>
    ///   <item>LazyIngressSinkRelay (für die EffectRunner-↔-SignalIngress-Zirkel-Dep)</item>
    ///   <item>EffectRunner (sink = Relay)</item>
    ///   <item>DecisionStepProcessor (initial state)</item>
    ///   <item>SessionTraceOrdinalProvider (seeded from max across SignalLog + Journal + Spool)</item>
    ///   <item>SignalIngress — Relay.Target = ingress</item>
    ///   <item>Scheduler.Fired → Ingress.Post-Bridge subscriben</item>
    ///   <item><c>signalIngress.Start()</c> — Worker-Thread läuft</item>
    ///   <item>Periodischer Drain-Loop starten (Fire-and-forget Task)</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Shutdown-Reihenfolge</b> (<see cref="Stop"/>, idempotent):
    /// <list type="number">
    ///   <item>Scheduler.Fired-Handler abmelden (keine neuen DeadlineFired-Signals)</item>
    ///   <item>DeadlineScheduler disposen (stoppt alle Timer)</item>
    ///   <item>SignalIngress.Stop — drainiert verbleibende Items</item>
    ///   <item>Drain-Loop Token cancellen, Task joinen</item>
    ///   <item>Terminaler Drain — finale Batches ans Backend</item>
    ///   <item>Snapshot.Save — letzter konsistenter State</item>
    ///   <item>Dispose aller Disposables</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class EnrollmentOrchestrator : IQuarantineSink, IDisposable
    {
        /// <summary>Default-Intervall zwischen periodischen Drain-Versuchen.</summary>
        public static readonly TimeSpan DefaultDrainInterval = TimeSpan.FromSeconds(30);

        /// <summary>Default Timeout für den Terminal-Drain beim Stop.</summary>
        public static readonly TimeSpan DefaultTerminalDrainTimeout = TimeSpan.FromSeconds(30);

        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly string _stateDirectory;
        private readonly string _transportDirectory;
        private readonly IClock _clock;
        private readonly AgentLogger _logger;
        private readonly IBackendTelemetryUploader _uploader;
        private readonly IReadOnlyList<IClassifier> _classifiers;
        private readonly IComponentFactory? _componentFactory;
        private readonly IReadOnlyCollection<string> _whiteGloveSealingPatternIds;
        private readonly int _channelCapacity;
        private readonly int _quarantineThreshold;
        private readonly TimeSpan _drainInterval;
        private readonly TimeSpan _terminalDrainTimeout;
        private readonly TimeSpan? _agentMaxLifetime;

        // Max-lifetime watchdog (M4.6.α). Timer is armed in Start() when _agentMaxLifetime != null.
        private System.Threading.Timer? _maxLifetimeTimer;
        private int _terminatedFired;

        // Built in Start()
        private DecisionEngine? _engine;
        private SignalLogWriter? _signalLog;
        private JournalWriter? _journal;
        private SnapshotPersistence? _snapshot;
        private EventSequencePersistence? _eventSequencePersistence;
        private EventSequenceCounter? _eventSequenceCounter;
        private TelemetrySpool? _spool;
        private TelemetryUploadOrchestrator? _transport;
        private TelemetrySignalEmitter? _signalEmitter;
        private TelemetryTransitionEmitter? _transitionEmitter;
        private EventTimelineEmitter? _timelineEmitter;
        private BackPressureEventObserver? _backPressureObserver;
        private DeadlineScheduler? _scheduler;
        private ClassifierRegistry? _classifierRegistry;
        private LazyIngressSinkRelay? _sinkRelay;
        private EffectRunner? _effectRunner;
        private DecisionStepProcessor? _processor;
        private SessionTraceOrdinalProvider? _traceCounter;
        private SignalIngress? _ingress;
        private IReadOnlyList<ICollectorHost>? _collectorHosts;

        private EventHandler<DeadlineFiredEventArgs>? _deadlineBridge;

        // Drain-Loop
        private CancellationTokenSource? _drainCts;
        private Task? _drainTask;

        // Lifecycle
        private int _started;
        private int _stopRequested;
        private int _disposed;

        // Quarantine flag — set mid-run, read on next start.
        private bool _quarantineRequested;
        private string? _quarantineReason;

        // Recovery flags (populated during Start()).
        private bool _isWhiteGlovePart2Resume;
        private bool _wasStartupQuarantine;

        public EnrollmentOrchestrator(
            string sessionId,
            string tenantId,
            string stateDirectory,
            string transportDirectory,
            IClock clock,
            AgentLogger logger,
            IBackendTelemetryUploader uploader,
            IEnumerable<IClassifier> classifiers,
            IComponentFactory? componentFactory = null,
            IReadOnlyCollection<string>? whiteGloveSealingPatternIds = null,
            int channelCapacity = SignalIngress.DefaultChannelCapacity,
            int quarantineThreshold = DecisionStepProcessor.DefaultQuarantineThreshold,
            TimeSpan? drainInterval = null,
            TimeSpan? terminalDrainTimeout = null,
            TimeSpan? agentMaxLifetime = null)
        {
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("SessionId is mandatory.", nameof(sessionId));
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentException("TenantId is mandatory.", nameof(tenantId));
            if (string.IsNullOrEmpty(stateDirectory)) throw new ArgumentException("StateDirectory is mandatory.", nameof(stateDirectory));
            if (string.IsNullOrEmpty(transportDirectory)) throw new ArgumentException("TransportDirectory is mandatory.", nameof(transportDirectory));

            _sessionId = sessionId;
            _tenantId = tenantId;
            _stateDirectory = stateDirectory;
            _transportDirectory = transportDirectory;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _uploader = uploader ?? throw new ArgumentNullException(nameof(uploader));

            if (classifiers == null) throw new ArgumentNullException(nameof(classifiers));
            var list = new List<IClassifier>();
            foreach (var c in classifiers)
            {
                if (c == null) throw new ArgumentException("Classifier enumerable must not contain null.", nameof(classifiers));
                list.Add(c);
            }
            _classifiers = list;
            _componentFactory = componentFactory;
            _whiteGloveSealingPatternIds = whiteGloveSealingPatternIds ?? Array.Empty<string>();

            _channelCapacity = channelCapacity;
            _quarantineThreshold = quarantineThreshold;
            _drainInterval = drainInterval ?? DefaultDrainInterval;
            _terminalDrainTimeout = terminalDrainTimeout ?? DefaultTerminalDrainTimeout;

            if (agentMaxLifetime.HasValue && agentMaxLifetime.Value <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(agentMaxLifetime), "AgentMaxLifetime must be positive when set.");
            _agentMaxLifetime = agentMaxLifetime;
        }

        /// <summary>
        /// Terminal event — fires once when the orchestrator declares the session done. Plan §4.x M4.6.α.
        /// <para>
        /// M4.6.α fires only <see cref="EnrollmentTerminationReason.MaxLifetimeExceeded"/>; the
        /// <see cref="EnrollmentTerminationReason.DecisionTerminalStage"/> path is wired in M4.6.β
        /// together with <c>CleanupService</c> self-destruct + SummaryDialog launch.
        /// </para>
        /// <para>
        /// Handlers may run on a ThreadPool thread (Timer callback). They must NOT call
        /// <see cref="Stop"/> directly (re-entrant) — raise a shutdown <see cref="ManualResetEventSlim"/>
        /// from the handler and let the main thread call <see cref="Stop"/>.
        /// </para>
        /// </summary>
        public event EventHandler<EnrollmentTerminatedEventArgs>? Terminated;

        // ---------------------------------------------------------------- Observability

        /// <summary>Aktueller <see cref="DecisionState"/> — nur nach <see cref="Start"/> verfügbar.</summary>
        public DecisionState CurrentState =>
            _processor?.CurrentState ?? throw new InvalidOperationException("Orchestrator not started.");

        /// <summary>True wenn ein Processor-Callback Quarantine-Eskalation ausgelöst hat. M4.4.5.f liest das.</summary>
        public bool IsQuarantineRequested => _quarantineRequested;

        /// <summary>Letzter Quarantine-Reason oder <c>null</c>.</summary>
        public string? QuarantineReason => _quarantineReason;

        /// <summary>
        /// <c>true</c> wenn der Start mit einem persistierten <c>WhiteGloveSealed</c>-State
        /// gelaufen ist und die Part-2-Bridge via <c>SessionRecovered</c>-Signal gezündet wurde.
        /// Plan §2.7 Sonderfall 1.
        /// </summary>
        public bool IsWhiteGlovePart2Resume => _isWhiteGlovePart2Resume;

        /// <summary>
        /// <c>true</c> wenn der Start auf einen korrupten State-Segment traf und
        /// Snapshot + Log-Segmente nach <c>.quarantine/{ts}/</c> bewegt wurden.
        /// Plan §2.7 Sonderfall 2.
        /// </summary>
        public bool WasStartupQuarantine => _wasStartupQuarantine;

        /// <summary>Exposed für Sub-c-Wiring (SignalAdapters + Collector-Callbacks).</summary>
        public ISignalIngressSink IngressSink =>
            (ISignalIngressSink?)_ingress ?? throw new InvalidOperationException("Orchestrator not started.");

        /// <summary>
        /// Exposed so Program.cs can subscribe to <see cref="TelemetryUploadOrchestrator.ServerResponseReceived"/>
        /// for M4.6.ε DeviceBlocked / DeviceKillSignal / AdminAction / Actions plumbing.
        /// </summary>
        public TelemetryUploadOrchestrator Transport =>
            _transport ?? throw new InvalidOperationException("Orchestrator not started.");

        // ---------------------------------------------------------------- Lifecycle

        /// <summary>
        /// Wires all components and starts the Ingress-Worker + periodischen Drain-Loop.
        /// Idempotent per <see cref="Interlocked.Exchange(ref int, int)"/> — zweiter Aufruf wirft.
        /// <para>
        /// <b><paramref name="onIngressReady"/></b> (single-rail refactor, plan §5.1): an optional
        /// caller hook invoked after the ingress worker is running but before any collector host
        /// is started. Use this slot to post agent-lifecycle signals (e.g. <c>agent_started</c>)
        /// so they land on the signal log — and therefore on the backend Events timeline — with
        /// sequence numbers lower than anything the collectors produce. The callback is invoked
        /// synchronously on the calling thread; exceptions are caught and logged so a malformed
        /// hook cannot abort Start. The WhiteGlove Part-2 recovery bridge (when applicable) fires
        /// first, then this hook, then collector hosts.
        /// </para>
        /// </summary>
        /// <param name="onIngressReady">Optional hook, invoked with the live <see cref="ISignalIngressSink"/> after ingress start and before collector start.</param>
        public void Start(Action<ISignalIngressSink>? onIngressReady = null)
        {
            if (Volatile.Read(ref _disposed) == 1) throw new ObjectDisposedException(nameof(EnrollmentOrchestrator));
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                throw new InvalidOperationException("EnrollmentOrchestrator already started.");
            }

            EnsureDirectories();

            // 1) Persistenz. Writer scannen bestehende Files im Ctor (LastOrdinal / LastStepIndex).
            var snapshotPath = Path.Combine(_stateDirectory, "snapshot.json");
            _signalLog = new SignalLogWriter(Path.Combine(_stateDirectory, "signal-log.jsonl"));
            _journal = new JournalWriter(Path.Combine(_stateDirectory, "journal.jsonl"));
            _snapshot = new SnapshotPersistence(snapshotPath, () => _clock.UtcNow);
            _eventSequencePersistence = new EventSequencePersistence(Path.Combine(_stateDirectory, "event-sequence.json"));

            // 2) Recovery (Plan §2.7 Sonderfälle 1+2).
            var snapshotFileExistsPreLoad = File.Exists(snapshotPath);
            var loadedState = _snapshot.Load();
            DecisionState initialState;

            if (loadedState == null && snapshotFileExistsPreLoad)
            {
                // Sonderfall 2: Snapshot-File existiert, Load lieferte null → Checksum-Mismatch
                // oder Deserialize-Failure. Plan §2.7c: Snapshot ist Cache, nicht Wahrheit —
                // Snapshot + Log-Segmente in Quarantäne, frisch starten.
                _logger.Error(
                    "EnrollmentOrchestrator: snapshot present but Load returned null (checksum mismatch or parse error) — quarantining state.");
                const string reason = "checksum-mismatch-on-startup";
                _snapshot.Quarantine(reason);
                SegmentQuarantine.QuarantineAll(_stateDirectory, reason, () => _clock.UtcNow);

                // Writer halten Pfade, nicht Handles — aber ihre in-memory Counter (LastOrdinal,
                // LastStepIndex) sind jetzt stale gegenüber den entfernten Files. Recreate, damit
                // Counter bei -1 starten.
                _signalLog = new SignalLogWriter(Path.Combine(_stateDirectory, "signal-log.jsonl"));
                _journal = new JournalWriter(Path.Combine(_stateDirectory, "journal.jsonl"));
                _eventSequencePersistence = new EventSequencePersistence(Path.Combine(_stateDirectory, "event-sequence.json"));

                initialState = DecisionState.CreateInitial(_sessionId, _tenantId);
                _wasStartupQuarantine = true;
            }
            else if (loadedState != null)
            {
                initialState = loadedState;
                _logger.Info(
                    $"EnrollmentOrchestrator: recovered state from snapshot (stage={loadedState.Stage}, stepIndex={loadedState.StepIndex}).");
            }
            else
            {
                initialState = DecisionState.CreateInitial(_sessionId, _tenantId);
                _logger.Info($"EnrollmentOrchestrator: fresh initial state for session {_sessionId}.");
            }

            // Sonderfall 1 Detection: WG Part 1 -> Reboot -> Part 2 Resume.
            // Self-destruct bleibt AUS (Caller ist zuständig — die Info wird via
            // IsWhiteGlovePart2Resume exponiert); Session-Recovered-Signal wird gepostet
            // nachdem Ingress gestartet ist.
            if (initialState.Stage == SessionStage.WhiteGloveSealed)
            {
                _isWhiteGlovePart2Resume = true;
                _logger.Info(
                    "EnrollmentOrchestrator: White-Glove Part-1 resume detected — SessionRecovered signal " +
                    "will be posted after ingress start; self-destruct handling is caller-owned.");
            }

            // 3) Telemetry-Transport.
            _spool = new TelemetrySpool(_transportDirectory, _clock, _logger);
            _transport = new TelemetryUploadOrchestrator(_spool, _uploader, _clock);

            // 4) Event-Emitter-Kette. Single-rail (Plan §5.10): der TelemetryEventEmitter wird
            //    lokal gebaut und nur in die zwei erlaubten Caller (EventTimelineEmitter,
            //    BackPressureEventObserver) injiziert. Kein Feld mehr auf dem Orchestrator —
            //    strukturelle Abhängigkeit erlischt damit, Architektur-Baseline-Test gate
            //    shrinks to exactly two permitted callers.
            _eventSequenceCounter = new EventSequenceCounter(_eventSequencePersistence);
            var eventEmitter = new TelemetryEventEmitter(_transport, _eventSequenceCounter, _sessionId, _tenantId);
            _signalEmitter = new TelemetrySignalEmitter(_transport, _sessionId, _tenantId);
            _transitionEmitter = new TelemetryTransitionEmitter(_transport, _sessionId, _tenantId);
            _timelineEmitter = new EventTimelineEmitter(eventEmitter);
            _backPressureObserver = new BackPressureEventObserver(eventEmitter, _clock);

            // 5) Deadlines + Classifiers.
            _scheduler = new DeadlineScheduler(_clock);
            _classifierRegistry = new ClassifierRegistry(_classifiers);

            // 6) Relay — EffectRunner braucht ISignalIngressSink, aber Ingress wird erst später
            //    gebaut. Relay löst den Zirkel über einen setzbaren Target-Pointer.
            _sinkRelay = new LazyIngressSinkRelay();

            // 7) EffectRunner.
            _effectRunner = new EffectRunner(
                scheduler: _scheduler,
                classifiers: _classifierRegistry,
                ingress: _sinkRelay,
                emitter: _timelineEmitter,
                snapshot: _snapshot,
                clock: _clock);

            // 8) Processor — owns the initial state + journal + snapshot + quarantine hook +
            //    M4.6.β terminal-stage hook (fires Terminated event when the engine reaches a
            //    terminal SessionStage).
            _processor = new DecisionStepProcessor(
                initialState: initialState,
                journal: _journal,
                effectRunner: _effectRunner,
                snapshot: _snapshot,
                quarantineSink: this,
                logger: _logger,
                quarantineThreshold: _quarantineThreshold,
                onTerminalStageReached: OnDecisionTerminalStage,
                transitionEmitter: _transitionEmitter);

            // 9) Trace-Ordinal. Recovery-seed = max(SignalLog, Journal, Spool) so restart after a
            //    crash never re-uses a SessionTraceOrdinal already persisted by a prior session.
            //    Every signal + transition goes through the Spool in M5+, but we still consult all
            //    three sources because:
            //      - SignalLog may contain signals whose spool-enqueue later failed (emit swallows
            //        transport exceptions per SignalIngress.cs §2.7c).
            //      - Journal likewise for transitions with failed spool-enqueue.
            //      - Spool.LastAssignedItemId is the single-source-of-truth for everything that
            //        reached the transport (including Events that bypass the provider entirely).
            var traceSeed = System.Math.Max(
                System.Math.Max(_signalLog.LastTraceOrdinal, _journal.LastTraceOrdinal),
                _spool.LastAssignedItemId);
            _traceCounter = new SessionTraceOrdinalProvider(seedLastAssigned: traceSeed);

            // 10) DecisionEngine + SignalIngress.
            _engine = new DecisionEngine();
            _ingress = new SignalIngress(
                engine: _engine,
                signalLog: _signalLog,
                traceCounter: _traceCounter,
                processor: _processor,
                clock: _clock,
                backPressureObserver: _backPressureObserver,
                signalEmitter: _signalEmitter,
                channelCapacity: _channelCapacity);

            // 11) Relay auf den echten Ingress umbiegen.
            _sinkRelay.Target = _ingress;

            // 12) Scheduler.Fired → synthetic DeadlineFired-Signal.
            _deadlineBridge = OnDeadlineFired;
            _scheduler.Fired += _deadlineBridge;

            // 13) Ingress-Worker starten (vor Collectors — sonst race-prone).
            _ingress.Start();

            // 13a) Recovery Sonderfall 1: WG Part-1 → Part-2 bridge zünden. Das Signal muss
            //      nach _ingress.Start laufen, aber VOR Collectors/Adapters — so sieht der
            //      Reducer-Worker zuerst die Bridge-Transition (WhiteGloveSealed → AwaitingUserSignIn)
            //      und erst danach kommende Real-Signals.
            if (_isWhiteGlovePart2Resume)
            {
                try
                {
                    _ingress.Post(
                        kind: DecisionSignalKind.SessionRecovered,
                        occurredAtUtc: _clock.UtcNow,
                        sourceOrigin: "EnrollmentOrchestrator",
                        evidence: new Evidence(
                            kind: EvidenceKind.Synthetic,
                            identifier: "wg_part1_resume",
                            summary: "White-Glove Part-1 state recovered from snapshot; Part-2 bridge triggered."),
                        payload: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["from"] = SessionStage.WhiteGloveSealed.ToString(),
                            ["to"] = SessionStage.WhiteGloveAwaitingUserSignIn.ToString(),
                        });
                }
                catch (Exception ex)
                {
                    _logger.Error("EnrollmentOrchestrator: failed to post SessionRecovered for WG Part-2 resume.", ex);
                }
            }

            // 13b) Caller-owned pre-collector hook. Single-rail refactor uses this slot to post
            //      agent-lifecycle signals (agent_started, agent_version_check, …) so they land
            //      on the signal log before any collector-generated signal — fixes the seq=13
            //      ordering regression from the V2 parity audit. Exceptions are caught so a
            //      malformed hook cannot abort Start or prevent collectors from running.
            if (onIngressReady != null)
            {
                try
                {
                    onIngressReady(_ingress);
                }
                catch (Exception ex)
                {
                    _logger.Error("EnrollmentOrchestrator: onIngressReady hook threw — continuing startup.", ex);
                }
            }

            // 13c) Plan §6 Fix 9 bootstrap — probe the FirstSync SkipUser/SkipDevice flags
            //      synchronously and post EspConfigDetected BEFORE any collector host starts.
            //      Rationale: DeviceInfoHost.CollectAll runs fire-and-forget on the ThreadPool,
            //      so on SkipUser=true enrollments the Shell-Core esp_exiting event can fire
            //      (via EspAndHelloHost which started here already) BEFORE the reducer has seen
            //      EspConfigDetected. Without this bootstrap, Fix 8's guard
            //      (ShouldTransitionToAwaitingHello) would block the legitimate AwaitingHello
            //      promotion because SkipUserEsp is still null in state — and the adapter's
            //      _finalizingPosted fire-once flag prevents a second attempt, leaving the
            //      session stuck in EspDeviceSetup/EspAccountSetup forever. Reducer has
            //      per-fact set-once semantics, so a later re-post from DeviceInfoCollector on
            //      CollectAll filling previously-missing facts is both allowed and safe.
            //      Always-on: production correctness > test convenience. Tests that count
            //      signals or transitions use <c>EspSkipConfigurationProbe.ScopedOverride</c>
            //      to force the probe to (null, null) which makes the bootstrap a no-op.
            PostEspConfigDetectedBootstrap();

            // 14) Collector-Hosts via Factory — nach Plan §5.10 (single-rail enforcement) gibt
            //     es keine Action<EnrollmentEvent>-Bridge mehr; jede Collector-Emission fließt
            //     über den Ingress als InformationalEvent.
            if (_componentFactory != null)
            {
                _collectorHosts = _componentFactory.CreateCollectorHosts(
                    _sessionId, _tenantId, _logger, _whiteGloveSealingPatternIds,
                    ingress: _ingress, clock: _clock);
                foreach (var host in _collectorHosts)
                {
                    try
                    {
                        host.Start();
                        _logger.Debug($"EnrollmentOrchestrator: started collector host '{host.Name}'.");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"EnrollmentOrchestrator: failed to start collector host '{host.Name}'.", ex);
                    }
                }
            }

            // 15) Periodischer Drain-Loop (Fire-and-forget).
            _drainCts = new CancellationTokenSource();
            _drainTask = Task.Run(() => DrainLoopAsync(_drainCts.Token));

            // 16) Max-lifetime watchdog (M4.6.α). Fires once after the configured duration when
            //     no real terminal stage has been reached. Timer is a best-effort System.Threading.Timer
            //     because VirtualClock-driven delay would not map to an OS-level wall-clock wait.
            if (_agentMaxLifetime.HasValue)
            {
                _maxLifetimeTimer = new System.Threading.Timer(
                    state: null,
                    dueTime: _agentMaxLifetime.Value,
                    period: System.Threading.Timeout.InfiniteTimeSpan,
                    callback: _ => RaiseMaxLifetimeExceeded());
                _logger.Info($"EnrollmentOrchestrator: max-lifetime watchdog armed ({_agentMaxLifetime.Value.TotalMinutes:F0}min).");
            }

            _logger.Info("EnrollmentOrchestrator: started.");
        }

        /// <summary>
        /// Plan §6 Fix 9 bootstrap. Probes <c>HKLM\SOFTWARE\Microsoft\Enrollments\{guid}\FirstSync</c>
        /// synchronously and posts <see cref="DecisionSignalKind.EspConfigDetected"/> so the
        /// reducer's <c>SkipUserEsp</c> / <c>SkipDeviceEsp</c> facts are set BEFORE any
        /// collector-driven <c>EspPhaseChanged(FinalizingSetup)</c> can arrive from
        /// <c>EspAndHelloHost</c>. No-op when neither flag can be read (FirstSync missing or
        /// registry probe fails) — the reducer's guards treat <c>null</c> as "unknown" and
        /// defensively keep the current stage, which is also the correct behavior in that
        /// edge case (the collector's subsequent <c>CollectAll</c> post will pick up the
        /// values once FirstSync populates).
        /// </summary>
        private void PostEspConfigDetectedBootstrap()
        {
            try
            {
                var (skipUser, skipDevice) = Monitoring.Enrollment.SystemSignals.EspSkipConfigurationProbe.Read(_logger);
                if (skipUser == null && skipDevice == null)
                {
                    _logger.Debug(
                        "EnrollmentOrchestrator: EspConfigDetected bootstrap skipped — FirstSync not yet populated; DeviceInfoCollector will post when CollectAll runs.");
                    return;
                }

                var payload = new Dictionary<string, string>(StringComparer.Ordinal);
                if (skipUser.HasValue)
                    payload[SignalPayloadKeys.SkipUserEsp] = skipUser.Value ? "true" : "false";
                if (skipDevice.HasValue)
                    payload[SignalPayloadKeys.SkipDeviceEsp] = skipDevice.Value ? "true" : "false";

                _ingress!.Post(
                    kind: DecisionSignalKind.EspConfigDetected,
                    occurredAtUtc: _clock.UtcNow,
                    sourceOrigin: "EnrollmentOrchestrator",
                    evidence: new Evidence(
                        kind: EvidenceKind.Raw,
                        identifier: "esp_config_detected_bootstrap",
                        summary: $"SkipUser={skipUser?.ToString() ?? "unknown"}, SkipDevice={skipDevice?.ToString() ?? "unknown"}",
                        derivationInputs: new Dictionary<string, string>(payload, StringComparer.Ordinal)
                        {
                            ["source"] = "registry_firstsync_bootstrap",
                        }),
                    payload: payload);

                _logger.Info(
                    $"EnrollmentOrchestrator: EspConfigDetected bootstrap posted (SkipUser={skipUser?.ToString() ?? "unknown"}, SkipDevice={skipDevice?.ToString() ?? "unknown"}).");
            }
            catch (Exception ex)
            {
                // Never fail Start over a bootstrap probe — the DeviceInfoCollector is the fallback.
                _logger.Error("EnrollmentOrchestrator: EspConfigDetected bootstrap threw — continuing startup.", ex);
            }
        }

        private void RaiseMaxLifetimeExceeded()
        {
            if (Volatile.Read(ref _stopRequested) == 1) return;
            if (Interlocked.Exchange(ref _terminatedFired, 1) == 1) return;

            _logger.Warning(
                $"EnrollmentOrchestrator: max-lifetime ({_agentMaxLifetime?.TotalMinutes:F0}min) exceeded — firing Terminated(MaxLifetimeExceeded).");

            var currentStage = _processor?.CurrentState?.Stage.ToString();

            var terminatedArgs = new EnrollmentTerminatedEventArgs(
                reason: EnrollmentTerminationReason.MaxLifetimeExceeded,
                outcome: EnrollmentTerminationOutcome.TimedOut,
                stageName: currentStage,
                terminatedAtUtc: _clock.UtcNow,
                details: $"Agent exceeded AgentMaxLifetimeMinutes cap ({_agentMaxLifetime?.TotalMinutes:F0}min) without reaching a terminal stage.");

            try { Terminated?.Invoke(this, terminatedArgs); }
            catch (Exception ex) { _logger.Error("EnrollmentOrchestrator: Terminated handler threw.", ex); }
        }

        /// <summary>
        /// M4.6.β — DecisionStepProcessor callback: the engine has transitioned the session
        /// into a terminal <see cref="SessionStage"/>. Fires the public <see cref="Terminated"/>
        /// event with <see cref="EnrollmentTerminationReason.DecisionTerminalStage"/> and an
        /// outcome derived from the stage (Completed/Part2→Succeeded, Failed→Failed,
        /// WhiteGloveSealed→Succeeded but with <see cref="SessionStageExtensions.IsPauseBeforePart2"/>
        /// signalled to callers via the stage name — callers decide whether to self-destruct).
        /// </summary>
        private void OnDecisionTerminalStage(DecisionState terminalState)
        {
            if (Volatile.Read(ref _stopRequested) == 1) return;
            if (Interlocked.Exchange(ref _terminatedFired, 1) == 1) return;

            // Stop the max-lifetime watchdog — the real terminal arrived before it could trip.
            try { _maxLifetimeTimer?.Dispose(); _maxLifetimeTimer = null; } catch { }

            var outcome = terminalState.Stage switch
            {
                SessionStage.Completed or SessionStage.WhiteGloveCompletedPart2 or SessionStage.WhiteGloveSealed
                    => EnrollmentTerminationOutcome.Succeeded,
                SessionStage.Failed => EnrollmentTerminationOutcome.Failed,
                _ => EnrollmentTerminationOutcome.TimedOut,
            };

            _logger.Info(
                $"EnrollmentOrchestrator: decision terminal stage reached (stage={terminalState.Stage}, outcome={outcome}) — firing Terminated(DecisionTerminalStage).");

            var terminatedArgs = new EnrollmentTerminatedEventArgs(
                reason: EnrollmentTerminationReason.DecisionTerminalStage,
                outcome: outcome,
                stageName: terminalState.Stage.ToString(),
                terminatedAtUtc: _clock.UtcNow,
                details: terminalState.Stage.IsPauseBeforePart2()
                    ? "WhiteGlove Part 1 sealed — session will resume on Part 2 post-reboot; self-destruct suppressed."
                    : null);

            try { Terminated?.Invoke(this, terminatedArgs); }
            catch (Exception ex) { _logger.Error("EnrollmentOrchestrator: Terminated handler threw.", ex); }
        }

        /// <summary>
        /// Stop the pipeline. Idempotent; calls <see cref="Stop"/> from <see cref="Dispose"/> as well.
        /// </summary>
        public void Stop()
        {
            if (Volatile.Read(ref _started) == 0) return;
            if (Interlocked.Exchange(ref _stopRequested, 1) == 1) return;

            _logger.Info("EnrollmentOrchestrator: stopping.");

            // -1) Max-lifetime watchdog stoppen (M4.6.α). Idempotent — safe even if never armed.
            try { _maxLifetimeTimer?.Dispose(); _maxLifetimeTimer = null; }
            catch (Exception ex) { _logger.Warning($"EnrollmentOrchestrator: max-lifetime timer dispose failed: {ex.Message}"); }

            // 0) Collector-Hosts stoppen — keine neuen Events / DecisionSignals aus dem Feld.
            if (_collectorHosts != null)
            {
                foreach (var host in _collectorHosts)
                {
                    try { host.Stop(); }
                    catch (Exception ex) { _logger.Warning($"EnrollmentOrchestrator: host '{host.Name}' stop failed: {ex.Message}"); }
                }
            }

            // 1) Scheduler.Fired-Handler abmelden — keine neuen DeadlineFired-Signals mehr.
            try
            {
                if (_scheduler != null && _deadlineBridge != null)
                {
                    _scheduler.Fired -= _deadlineBridge;
                }
            }
            catch (Exception ex) { _logger.Warning($"EnrollmentOrchestrator: unsubscribe scheduler failed: {ex.Message}"); }

            // 2) Scheduler disposen — stoppt alle aktiven Timer.
            try { _scheduler?.Dispose(); }
            catch (Exception ex) { _logger.Warning($"EnrollmentOrchestrator: scheduler dispose failed: {ex.Message}"); }

            // 3) SignalIngress stoppen — drainiert verbleibende Items, wartet auf Worker-Join.
            try { _ingress?.Stop(); }
            catch (Exception ex) { _logger.Warning($"EnrollmentOrchestrator: ingress stop failed: {ex.Message}"); }

            // 4) Drain-Loop Token cancellen + warten.
            try
            {
                _drainCts?.Cancel();
                _drainTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException) { /* wrapped OperationCanceledException */ }
            catch (Exception ex) { _logger.Warning($"EnrollmentOrchestrator: drain loop join failed: {ex.Message}"); }

            // 5) Terminaler Drain — finale Batches.
            try
            {
                using (var timeoutCts = new CancellationTokenSource(_terminalDrainTimeout))
                {
                    _transport?.DrainAllAsync(timeoutCts.Token).GetAwaiter().GetResult();
                }
            }
            catch (Exception ex) { _logger.Warning($"EnrollmentOrchestrator: terminal drain failed: {ex.Message}"); }

            // 6) Finales Snapshot.
            try
            {
                if (_processor != null && _snapshot != null)
                {
                    _snapshot.Save(_processor.CurrentState);
                }
            }
            catch (Exception ex) { _logger.Warning($"EnrollmentOrchestrator: final snapshot save failed: {ex.Message}"); }

            // 7) Dispose aller Disposables.
            if (_collectorHosts != null)
            {
                foreach (var host in _collectorHosts)
                {
                    try { host.Dispose(); } catch { /* best-effort */ }
                }
            }
            try { _transport?.Dispose(); } catch { /* best-effort */ }
            try { _ingress?.Dispose(); } catch { /* best-effort */ }
            try { _drainCts?.Dispose(); } catch { /* best-effort */ }

            _logger.Info("EnrollmentOrchestrator: stopped.");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { Stop(); }
            catch { /* Dispose darf nicht werfen. */ }
        }

        // ---------------------------------------------------------------- IQuarantineSink

        public void TriggerQuarantine(string reason)
        {
            _quarantineRequested = true;
            _quarantineReason = reason ?? string.Empty;
            _logger.Error($"EnrollmentOrchestrator: quarantine requested — {reason}");
            // Actual segment-quarantine happens on next Start (M4.4.5.f), not mid-run.
        }

        // ---------------------------------------------------------------- Test helpers

        /// <summary>
        /// Explicit drain for tests. Returns the <see cref="DrainResult"/> of one flush cycle.
        /// </summary>
        internal Task<DrainResult> DrainAsync(CancellationToken cancellationToken = default)
        {
            if (_transport == null) throw new InvalidOperationException("Orchestrator not started.");
            return _transport.DrainAllAsync(cancellationToken);
        }

        // ---------------------------------------------------------------- Private

        private void EnsureDirectories()
        {
            if (!Directory.Exists(_stateDirectory)) Directory.CreateDirectory(_stateDirectory);
            if (!Directory.Exists(_transportDirectory)) Directory.CreateDirectory(_transportDirectory);
        }

        private void OnDeadlineFired(object? sender, DeadlineFiredEventArgs e)
        {
            if (Volatile.Read(ref _stopRequested) == 1 || _ingress == null) return;

            try
            {
                var evidence = new Evidence(
                    kind: EvidenceKind.Synthetic,
                    identifier: e.Deadline.Name,
                    summary: $"deadline '{e.Deadline.Name}' fired at {e.Deadline.DueAtUtc:O}");

                var payload = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["deadlineName"] = e.Deadline.Name,
                };

                // OccurredAtUtc = DueAtUtc (not firedAt) — replay-determinism per DeadlineFiredEventArgs doc.
                _ingress.Post(
                    kind: DecisionSignalKind.DeadlineFired,
                    occurredAtUtc: e.Deadline.DueAtUtc,
                    sourceOrigin: "DeadlineScheduler",
                    evidence: evidence,
                    payload: payload);
            }
            catch (Exception ex)
            {
                // Deadline-Bridge darf den Scheduler-Thread nicht killen.
                _logger.Error($"EnrollmentOrchestrator: failed to post DeadlineFired for '{e.Deadline.Name}'.", ex);
            }
        }

        private async Task DrainLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        // Wall-clock delay, NOT _clock.Delay: drain is a real network cadence
                        // independent of the decision-engine's logical time. VirtualClock.Delay
                        // returns immediately and would cause a tight loop in unit tests.
                        await Task.Delay(_drainInterval, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { return; }

                    if (ct.IsCancellationRequested) return;

                    try
                    {
                        var result = await _transport!.DrainAllAsync(ct).ConfigureAwait(false);
                        if (result.FailedBatches > 0)
                        {
                            _logger.Warning(
                                $"EnrollmentOrchestrator: periodic drain had {result.FailedBatches} failed batch(es); " +
                                $"last error: {result.LastErrorReason}");
                        }
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        _logger.Error("EnrollmentOrchestrator: periodic drain threw.", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("EnrollmentOrchestrator: drain loop faulted.", ex);
            }
        }

        /// <summary>
        /// Pass-through <see cref="ISignalIngressSink"/> to break the Effect-↔-Ingress ctor
        /// cycle: EffectRunner requires a sink, but the real Ingress wants the processor +
        /// effect-runner to already exist. Relay is constructed first, wired to the real
        /// Ingress last.
        /// </summary>
        private sealed class LazyIngressSinkRelay : ISignalIngressSink
        {
            internal ISignalIngressSink? Target { get; set; }

            public void Post(
                DecisionSignalKind kind,
                DateTime occurredAtUtc,
                string sourceOrigin,
                Evidence evidence,
                IReadOnlyDictionary<string, string>? payload = null,
                int kindSchemaVersion = 1,
                object? typedPayload = null)
            {
                var t = Target;
                if (t == null)
                {
                    throw new InvalidOperationException(
                        "LazyIngressSinkRelay.Target is null — SignalIngress was not wired yet.");
                }
                t.Post(kind, occurredAtUtc, sourceOrigin, evidence, payload, kindSchemaVersion, typedPayload);
            }
        }
    }
}
