#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Telemetry.Events;
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
    ///   <item>SessionTraceOrdinalProvider (TODO M4.4.5.f: seed aus persisted max)</item>
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
        private readonly int _channelCapacity;
        private readonly int _quarantineThreshold;
        private readonly TimeSpan _drainInterval;
        private readonly TimeSpan _terminalDrainTimeout;

        // Built in Start()
        private DecisionEngine? _engine;
        private SignalLogWriter? _signalLog;
        private JournalWriter? _journal;
        private SnapshotPersistence? _snapshot;
        private EventSequencePersistence? _eventSequencePersistence;
        private EventSequenceCounter? _eventSequenceCounter;
        private TelemetrySpool? _spool;
        private TelemetryUploadOrchestrator? _transport;
        private TelemetryEventEmitter? _eventEmitter;
        private EventTimelineEmitter? _timelineEmitter;
        private BackPressureEventObserver? _backPressureObserver;
        private DeadlineScheduler? _scheduler;
        private ClassifierRegistry? _classifierRegistry;
        private LazyIngressSinkRelay? _sinkRelay;
        private EffectRunner? _effectRunner;
        private DecisionStepProcessor? _processor;
        private SessionTraceOrdinalProvider? _traceCounter;
        private SignalIngress? _ingress;

        private EventHandler<DeadlineFiredEventArgs>? _deadlineBridge;

        // Drain-Loop
        private CancellationTokenSource? _drainCts;
        private Task? _drainTask;

        // Lifecycle
        private int _started;
        private int _stopRequested;
        private int _disposed;

        // Quarantine flag — M4.4.5.f reads this on next start.
        private bool _quarantineRequested;
        private string? _quarantineReason;

        public EnrollmentOrchestrator(
            string sessionId,
            string tenantId,
            string stateDirectory,
            string transportDirectory,
            IClock clock,
            AgentLogger logger,
            IBackendTelemetryUploader uploader,
            IEnumerable<IClassifier> classifiers,
            int channelCapacity = SignalIngress.DefaultChannelCapacity,
            int quarantineThreshold = DecisionStepProcessor.DefaultQuarantineThreshold,
            TimeSpan? drainInterval = null,
            TimeSpan? terminalDrainTimeout = null)
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

            _channelCapacity = channelCapacity;
            _quarantineThreshold = quarantineThreshold;
            _drainInterval = drainInterval ?? DefaultDrainInterval;
            _terminalDrainTimeout = terminalDrainTimeout ?? DefaultTerminalDrainTimeout;
        }

        // ---------------------------------------------------------------- Observability

        /// <summary>Aktueller <see cref="DecisionState"/> — nur nach <see cref="Start"/> verfügbar.</summary>
        public DecisionState CurrentState =>
            _processor?.CurrentState ?? throw new InvalidOperationException("Orchestrator not started.");

        /// <summary>True wenn ein Processor-Callback Quarantine-Eskalation ausgelöst hat. M4.4.5.f liest das.</summary>
        public bool IsQuarantineRequested => _quarantineRequested;

        /// <summary>Letzter Quarantine-Reason oder <c>null</c>.</summary>
        public string? QuarantineReason => _quarantineReason;

        /// <summary>Exposed für Sub-c-Wiring (SignalAdapters + Collector-Callbacks).</summary>
        public ISignalIngressSink IngressSink =>
            (ISignalIngressSink?)_ingress ?? throw new InvalidOperationException("Orchestrator not started.");

        /// <summary>Exposed für Sub-c-Wiring (Collector <c>onEventCollected</c>-Bridge).</summary>
        public TelemetryEventEmitter EventEmitter =>
            _eventEmitter ?? throw new InvalidOperationException("Orchestrator not started.");

        // ---------------------------------------------------------------- Lifecycle

        /// <summary>
        /// Wires all components and starts the Ingress-Worker + periodischen Drain-Loop.
        /// Idempotent per <see cref="Interlocked.Exchange(ref int, int)"/> — zweiter Aufruf wirft.
        /// </summary>
        public void Start()
        {
            if (Volatile.Read(ref _disposed) == 1) throw new ObjectDisposedException(nameof(EnrollmentOrchestrator));
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                throw new InvalidOperationException("EnrollmentOrchestrator already started.");
            }

            EnsureDirectories();

            // 1) Persistenz.
            _signalLog = new SignalLogWriter(Path.Combine(_stateDirectory, "signal-log.jsonl"));
            _journal = new JournalWriter(Path.Combine(_stateDirectory, "journal.jsonl"));
            _snapshot = new SnapshotPersistence(Path.Combine(_stateDirectory, "snapshot.json"), () => _clock.UtcNow);
            _eventSequencePersistence = new EventSequencePersistence(Path.Combine(_stateDirectory, "event-sequence.json"));

            // 2) Initial state — Recovery-Vorstufe. Sub-f macht Checksum/Replay-Handling rund.
            var initialState = _snapshot.Load();
            if (initialState == null)
            {
                initialState = DecisionState.CreateInitial(_sessionId, _tenantId);
                _logger.Info($"EnrollmentOrchestrator: fresh initial state for session {_sessionId}.");
            }
            else
            {
                _logger.Info($"EnrollmentOrchestrator: recovered state from snapshot (stage={initialState.Stage}, stepIndex={initialState.StepIndex}).");
            }

            // 3) Telemetry-Transport.
            _spool = new TelemetrySpool(_transportDirectory, _clock);
            _transport = new TelemetryUploadOrchestrator(_spool, _uploader, _clock);

            // 4) Event-Emitter-Kette.
            _eventSequenceCounter = new EventSequenceCounter(_eventSequencePersistence);
            _eventEmitter = new TelemetryEventEmitter(_transport, _eventSequenceCounter, _sessionId, _tenantId);
            _timelineEmitter = new EventTimelineEmitter(_eventEmitter);
            _backPressureObserver = new BackPressureEventObserver(_eventEmitter, _clock);

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

            // 8) Processor — owns the initial state + journal + snapshot + quarantine hook.
            _processor = new DecisionStepProcessor(
                initialState: initialState,
                journal: _journal,
                effectRunner: _effectRunner,
                snapshot: _snapshot,
                quarantineSink: this,
                logger: _logger,
                quarantineThreshold: _quarantineThreshold);

            // 9) Trace-Ordinal. TODO(M4.4.5.f): seed aus max(SignalLog.LastOrdinal,
            //    Journal.LastStepIndex, Spool.LastAssignedItemId via SessionTraceOrdinal).
            _traceCounter = new SessionTraceOrdinalProvider();

            // 10) DecisionEngine + SignalIngress.
            _engine = new DecisionEngine();
            _ingress = new SignalIngress(
                engine: _engine,
                signalLog: _signalLog,
                traceCounter: _traceCounter,
                processor: _processor,
                clock: _clock,
                backPressureObserver: _backPressureObserver,
                channelCapacity: _channelCapacity);

            // 11) Relay auf den echten Ingress umbiegen.
            _sinkRelay.Target = _ingress;

            // 12) Scheduler.Fired → synthetic DeadlineFired-Signal.
            _deadlineBridge = OnDeadlineFired;
            _scheduler.Fired += _deadlineBridge;

            // 13) Ingress-Worker starten.
            _ingress.Start();

            // 14) Periodischer Drain-Loop (Fire-and-forget).
            _drainCts = new CancellationTokenSource();
            _drainTask = Task.Run(() => DrainLoopAsync(_drainCts.Token));

            _logger.Info("EnrollmentOrchestrator: started.");
        }

        /// <summary>
        /// Stop the pipeline. Idempotent; calls <see cref="Stop"/> from <see cref="Dispose"/> as well.
        /// </summary>
        public void Stop()
        {
            if (Volatile.Read(ref _started) == 0) return;
            if (Interlocked.Exchange(ref _stopRequested, 1) == 1) return;

            _logger.Info("EnrollmentOrchestrator: stopping.");

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
                int kindSchemaVersion = 1)
            {
                var t = Target;
                if (t == null)
                {
                    throw new InvalidOperationException(
                        "LazyIngressSinkRelay.Target is null — SignalIngress was not wired yet.");
                }
                t.Post(kind, occurredAtUtc, sourceOrigin, evidence, payload, kindSchemaVersion);
            }
        }
    }
}
