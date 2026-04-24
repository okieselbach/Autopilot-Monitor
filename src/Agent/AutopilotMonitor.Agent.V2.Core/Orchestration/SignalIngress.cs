#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Telemetry.Signals;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Single-writer Signal-Ingress. Plan §2.1a / §2.7c / L.1.
    /// <para>
    /// Alle Collector-Threads (und der <see cref="EffectRunner"/> via
    /// <see cref="ISignalIngressSink"/>) rufen <see cref="Post"/>. Ein dedizierter Worker-Thread
    /// liest die Items sequenziell, vergibt <c>SessionSignalOrdinal</c> +
    /// <c>SessionTraceOrdinal</c>, appendet ins SignalLog (Sofort-Flush) und delegiert den
    /// Reduce+Apply-Schritt an <see cref="IDecisionStepProcessor"/>.
    /// </para>
    /// <para>
    /// <b>Back-Pressure</b>: bounded <see cref="BlockingCollection{T}"/> (default 256) —
    /// volle Channel blockiert den Producer beim <c>Add</c>. Der Ingress misst die Block-Dauer
    /// und meldet sie throttled (1×/min/origin) an <see cref="IBackPressureObserver"/>.
    /// Die Back-Pressure-Emission läuft NICHT durch den Signal-Channel (würde deadlocken).
    /// </para>
    /// <para>
    /// <b>Shutdown</b>: <see cref="Stop"/> ruft <see cref="BlockingCollection{T}.CompleteAdding"/>,
    /// wartet auf das Drain-Ende via <c>Thread.Join</c>. Nach <c>Stop</c> wirft jeder
    /// weitere <see cref="Post"/> <see cref="InvalidOperationException"/>.
    /// </para>
    /// </summary>
    public sealed class SignalIngress : ISignalIngressSink, IDisposable
    {
        /// <summary>Plan §2.1a — Default-Channel-Kapazität.</summary>
        public const int DefaultChannelCapacity = 256;

        /// <summary>Plan §2.1a — max. 1 Back-Pressure-Event pro Minute pro Origin.</summary>
        public static readonly TimeSpan DefaultBackPressureThrottle = TimeSpan.FromMinutes(1);

        private readonly IDecisionEngine _engine;
        private readonly ISignalLogWriter _signalLog;
        private readonly ISessionTraceOrdinalProvider _traceCounter;
        private readonly IDecisionStepProcessor _processor;
        private readonly IBackPressureObserver? _observer;
        private readonly TelemetrySignalEmitter? _signalEmitter;
        private readonly IClock _clock;
        private readonly TimeSpan _backPressureThrottle;
        private readonly int _channelCapacity;

        private readonly BlockingCollection<IngressItem> _channel;
        private readonly Dictionary<string, DateTime> _lastBackPressureEmittedUtc =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly object _backPressureLock = new object();

        private Thread? _worker;
        private long _nextSignalOrdinal;     // worker-thread-only after Start; seeded from SignalLog
        private int _started;                 // 0 = not started, 1 = running
        private int _stopRequested;           // 0 = live, 1 = Stop called
        private Exception? _workerFault;     // captured if worker dies unexpectedly

        public SignalIngress(
            IDecisionEngine engine,
            ISignalLogWriter signalLog,
            ISessionTraceOrdinalProvider traceCounter,
            IDecisionStepProcessor processor,
            IClock clock,
            IBackPressureObserver? backPressureObserver = null,
            TelemetrySignalEmitter? signalEmitter = null,
            int channelCapacity = DefaultChannelCapacity,
            TimeSpan? backPressureThrottle = null)
        {
            if (channelCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(channelCapacity), "Channel capacity must be > 0.");
            }

            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _signalLog = signalLog ?? throw new ArgumentNullException(nameof(signalLog));
            _traceCounter = traceCounter ?? throw new ArgumentNullException(nameof(traceCounter));
            _processor = processor ?? throw new ArgumentNullException(nameof(processor));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _observer = backPressureObserver;
            _signalEmitter = signalEmitter;
            _channelCapacity = channelCapacity;
            _backPressureThrottle = backPressureThrottle ?? DefaultBackPressureThrottle;

            _channel = new BlockingCollection<IngressItem>(boundedCapacity: channelCapacity);
            _nextSignalOrdinal = signalLog.LastOrdinal + 1;
        }

        /// <summary>Zuletzt vergebener <c>SessionSignalOrdinal</c>; <c>-1</c> bevor das erste Signal verarbeitet wurde.</summary>
        public long LastAssignedSignalOrdinal => Interlocked.Read(ref _nextSignalOrdinal) - 1;

        /// <summary>Konfigurierte Channel-Kapazität.</summary>
        public int ChannelCapacity => _channelCapacity;

        /// <summary>Aktuelle Queue-Länge. Nur für Observability/Tests — enthält Race-Fenster.</summary>
        public int ApproximateQueueLength => _channel.Count;

        /// <summary>
        /// Fires synchronously from <see cref="Post"/> after a signal has been accepted into
        /// the channel (for both the fast-path <see cref="BlockingCollection{T}.TryAdd"/> and
        /// the back-pressured <c>Add</c> slow-path). Exposed so idle-activity observers — e.g.
        /// <c>PeriodicCollectorLifecycleHost</c> — can see <b>every</b> signal, regardless of
        /// which <c>InformationalEventPost</c> instance posted it. Codex Finding 4.
        /// <para>
        /// <b>Handler contract</b>: must be fast and must not throw. Exceptions are caught and
        /// swallowed so a buggy observer cannot disrupt the production ingress path.
        /// </para>
        /// </summary>
        public event Action<DecisionSignalKind, IReadOnlyDictionary<string, string>?>? SignalPosted;

        /// <summary>
        /// Startet den Worker-Thread. Muss vor dem ersten <see cref="Post"/> aufgerufen werden.
        /// Mehrfach-Aufruf wirft.
        /// </summary>
        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                throw new InvalidOperationException("SignalIngress already started.");
            }

            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "SignalIngress.Worker",
            };
            _worker.Start();
        }

        /// <summary>
        /// Signalisiert Shutdown, drained verbleibende Items und joined den Worker-Thread.
        /// Idempotent — zweiter Aufruf ist ein No-Op. Wirft, wenn der Worker-Thread eine
        /// unerwartete Exception gefangen hat.
        /// </summary>
        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopRequested, 1) == 1)
            {
                return;
            }

            // CompleteAdding unblockt GetConsumingEnumerable sobald die Queue leer ist.
            _channel.CompleteAdding();

            var worker = _worker;
            if (worker != null && worker.IsAlive)
            {
                worker.Join();
            }

            if (_workerFault != null)
            {
                throw new InvalidOperationException(
                    "SignalIngress worker faulted unexpectedly.", _workerFault);
            }
        }

        public void Dispose()
        {
            try { Stop(); }
            catch { /* Dispose darf nicht werfen. */ }
            _channel.Dispose();
        }

        /// <summary>
        /// Reicht ein Raw-/Derived-/Synthetic-Signal in den Ingress ein. Thread-safe; darf von
        /// beliebig vielen Collector-Threads (und vom <see cref="EffectRunner"/>) parallel
        /// gerufen werden. Blockiert, wenn der Channel voll ist — Plan §2.1a Back-Pressure.
        /// </summary>
        public void Post(
            DecisionSignalKind kind,
            DateTime occurredAtUtc,
            string sourceOrigin,
            Evidence evidence,
            IReadOnlyDictionary<string, string>? payload = null,
            int kindSchemaVersion = 1,
            object? typedPayload = null)
        {
            if (Volatile.Read(ref _stopRequested) == 1 || _channel.IsAddingCompleted)
            {
                throw new InvalidOperationException("SignalIngress has been stopped.");
            }
            if (evidence == null)
            {
                throw new ArgumentNullException(nameof(evidence));
            }
            if (string.IsNullOrEmpty(sourceOrigin))
            {
                throw new ArgumentException("SourceOrigin is mandatory.", nameof(sourceOrigin));
            }

            var item = new IngressItem(kind, kindSchemaVersion, occurredAtUtc, sourceOrigin, evidence, payload, typedPayload);

            // Fast path: non-blocking enqueue.
            if (_channel.TryAdd(item))
            {
                RaiseSignalPosted(kind, payload);
                return;
            }

            // Slow path: bounded channel full → block, measure duration, notify observer throttled.
            var blockStartedUtc = _clock.UtcNow;
            int queueLenAtBlock = _channel.Count;
            try
            {
                _channel.Add(item);
            }
            catch (InvalidOperationException)
            {
                // CompleteAdding wurde zwischen TryAdd und Add gerufen.
                throw new InvalidOperationException("SignalIngress has been stopped.");
            }

            var blockedFor = _clock.UtcNow - blockStartedUtc;
            if (blockedFor < TimeSpan.Zero)
            {
                blockedFor = TimeSpan.Zero;
            }

            NotifyBackPressureThrottled(sourceOrigin, queueLenAtBlock, blockedFor);
            RaiseSignalPosted(kind, payload);
        }

        private void RaiseSignalPosted(DecisionSignalKind kind, IReadOnlyDictionary<string, string>? payload)
        {
            // Snapshot the delegate once — standard pattern to tolerate concurrent subscribe /
            // unsubscribe around the invocation.
            var handler = SignalPosted;
            if (handler == null) return;
            try { handler(kind, payload); }
            catch
            {
                // Observer exceptions MUST NOT disrupt the production ingress path. The idle-
                // activity observer (Codex Finding 4) is advisory — dropping one observation
                // is strictly better than surfacing a mid-Post exception to a collector thread.
            }
        }

        private void NotifyBackPressureThrottled(string origin, int queueLengthAtBlock, TimeSpan blockedFor)
        {
            if (_observer == null)
            {
                return;
            }

            var now = _clock.UtcNow;
            lock (_backPressureLock)
            {
                if (_lastBackPressureEmittedUtc.TryGetValue(origin, out var last) &&
                    (now - last) < _backPressureThrottle)
                {
                    return;
                }
                _lastBackPressureEmittedUtc[origin] = now;
            }

            try
            {
                _observer.OnBackPressure(origin, _channelCapacity, queueLengthAtBlock, blockedFor);
            }
            catch
            {
                // Observer-Fehler dürfen den Produktions-Pfad nicht aufhalten. Weglucken.
            }
        }

        private void WorkerLoop()
        {
            try
            {
                foreach (var item in _channel.GetConsumingEnumerable())
                {
                    ProcessItem(item);
                }
            }
            catch (Exception ex)
            {
                // Darf nicht passieren — ProcessItem fängt alles ab. Falls doch, für Stop()
                // aufheben, damit die Regression sichtbar ist.
                _workerFault = ex;
            }
        }

        private void ProcessItem(IngressItem item)
        {
            // Single-writer: _nextSignalOrdinal wird ausschließlich hier verändert.
            long signalOrdinal = _nextSignalOrdinal;
            long traceOrdinal = _traceCounter.Next();

            DecisionSignal signal;
            try
            {
                signal = new DecisionSignal(
                    sessionSignalOrdinal: signalOrdinal,
                    sessionTraceOrdinal: traceOrdinal,
                    kind: item.Kind,
                    kindSchemaVersion: item.KindSchemaVersion,
                    occurredAtUtc: item.OccurredAtUtc,
                    sourceOrigin: item.SourceOrigin,
                    evidence: item.Evidence,
                    payload: item.Payload,
                    typedPayload: item.TypedPayload);
            }
            catch
            {
                // DecisionSignal-Ctor validiert Pflichtfelder. Invalid input → überspringen,
                // Ordinal NICHT verbrauchen (sonst entsteht eine Lücke im SignalLog).
                return;
            }

            try
            {
                _signalLog.Append(signal);
            }
            catch
            {
                // Append fehlgeschlagen (z.B. Disk-Full). Ohne on-disk-Persistenz darf der
                // Reducer nicht laufen (L.1 Signal-Log-Determinismus). Ordinal bleibt „geplant",
                // wird beim nächsten Versuch neu vergeben — monoton, da _nextSignalOrdinal
                // noch nicht inkrementiert wurde.
                return;
            }

            _nextSignalOrdinal = signalOrdinal + 1;

            // Project the signal onto the telemetry transport for backend upload. Local
            // SignalLog is authoritative (§2.7c / L.1); a transport enqueue failure must NOT
            // break the ingress worker. Spool overflow / serialization faults get swallowed
            // here so the reducer path below still runs on the just-committed local signal.
            if (_signalEmitter != null)
            {
                try { _signalEmitter.Emit(signal); }
                catch { /* best-effort upload; local state already consistent */ }
            }

            var state = _processor.CurrentState;
            // IDecisionEngine.Reduce ist fail-safe (DecisionEngine.cs:39-47): wirft nicht.
            var step = _engine.Reduce(state, signal);

            EffectRunResult? effectResult = null;
            try
            {
                effectResult = _processor.ApplyStep(step, signal);
            }
            catch
            {
                // Processor-Fehler (z.B. Journal-Write, Effect-Transient-Exhaust) werden in
                // M4.4.5 vom Orchestrator geloggt + ggf. Quarantine ausgelöst. M4.4.0
                // verschluckt bewusst, damit der Ingress-Worker nicht verstummt.
            }

            // Codex follow-up (post-#50 #B): inline durable-abort path. When the step's
            // effects signal a critical infrastructure failure (e.g. timer scheduler died)
            // we must land a synthetic EffectInfrastructureFailure signal on the SignalLog
            // BEFORE returning to the worker loop — otherwise a crash between here and the
            // next Post() would leave recovery replaying the phantom deadline state with no
            // terminal signal to bridge it to SessionStage.Failed.
            //
            // The recursion guard prevents processing an abort that the synthetic signal's
            // own reducer handler might somehow report (shouldn't happen — HandleEffect-
            // InfrastructureFailureV1 transitions to terminal without queuing critical
            // effects — but the guard makes that invariant failure-safe).
            if (effectResult != null
                && effectResult.SessionMustAbort
                && signal.Kind != DecisionSignalKind.EffectInfrastructureFailure)
            {
                ProcessInlineAbortSignal(effectResult.AbortReason, signal.OccurredAtUtc);
            }
        }

        /// <summary>
        /// Synthesise + durably persist + reduce a <c>EffectInfrastructureFailure</c> signal
        /// inline within the current worker iteration. See ProcessItem for the rationale —
        /// this closes the crash-window identified by Codex follow-up post-#50 #B.
        /// </summary>
        private void ProcessInlineAbortSignal(string? abortReason, DateTime occurredAtUtc)
        {
            long signalOrdinal = _nextSignalOrdinal;
            long traceOrdinal = _traceCounter.Next();

            DecisionSignal syntheticSignal;
            try
            {
                syntheticSignal = new DecisionSignal(
                    sessionSignalOrdinal: signalOrdinal,
                    sessionTraceOrdinal: traceOrdinal,
                    kind: DecisionSignalKind.EffectInfrastructureFailure,
                    kindSchemaVersion: 1,
                    occurredAtUtc: occurredAtUtc,
                    sourceOrigin: "signal-ingress:inline-abort",
                    evidence: new Evidence(
                        kind: EvidenceKind.Synthetic,
                        identifier: "effect_infrastructure_failure:inline",
                        summary: $"Inline abort synthesised by SignalIngress: {abortReason ?? "<unspecified>"}"),
                    payload: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["reason"] = abortReason ?? string.Empty,
                    });
            }
            catch
            {
                // DecisionSignal ctor validation must not fail for a locally-constructed
                // signal, but defensively bail — the max-lifetime watchdog remains the
                // last-resort termination path.
                return;
            }

            try
            {
                _signalLog.Append(syntheticSignal);
            }
            catch
            {
                // Persist-Failure — we must NOT advance the ordinal (log gap) and cannot
                // safely reduce. Recovery will see the phantom state; the watchdog will
                // clean up the hung session.
                return;
            }

            _nextSignalOrdinal = signalOrdinal + 1;

            if (_signalEmitter != null)
            {
                try { _signalEmitter.Emit(syntheticSignal); }
                catch { /* best-effort upload */ }
            }

            var state = _processor.CurrentState;
            var step = _engine.Reduce(state, syntheticSignal);

            try
            {
                _processor.ApplyStep(step, syntheticSignal);
            }
            catch
            {
                // Processor fault on the terminal transition — Journal has the abort
                // transition committed (reducer produces Stage=Failed unconditionally).
                // Swallowed by convention; worker must not die from this path.
            }
        }

        private readonly struct IngressItem
        {
            public IngressItem(
                DecisionSignalKind kind,
                int kindSchemaVersion,
                DateTime occurredAtUtc,
                string sourceOrigin,
                Evidence evidence,
                IReadOnlyDictionary<string, string>? payload,
                object? typedPayload)
            {
                Kind = kind;
                KindSchemaVersion = kindSchemaVersion;
                OccurredAtUtc = occurredAtUtc;
                SourceOrigin = sourceOrigin;
                Evidence = evidence;
                Payload = payload;
                TypedPayload = typedPayload;
            }

            public DecisionSignalKind Kind { get; }
            public int KindSchemaVersion { get; }
            public DateTime OccurredAtUtc { get; }
            public string SourceOrigin { get; }
            public Evidence Evidence { get; }
            public IReadOnlyDictionary<string, string>? Payload { get; }
            public object? TypedPayload { get; }
        }
    }
}
