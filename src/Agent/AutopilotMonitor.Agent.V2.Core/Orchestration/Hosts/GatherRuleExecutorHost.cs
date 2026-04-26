#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    internal sealed class GatherRuleExecutorHost : ICollectorHost
    {
        public string Name => "GatherRuleExecutor";

        private readonly Monitoring.Telemetry.Gather.GatherRuleExecutor _executor;
        private readonly List<GatherRule> _rules;
        private readonly AgentLogger _logger;
        private readonly bool _unrestrictedMode;
        private int _disposed;

        public GatherRuleExecutorHost(
            string sessionId,
            string tenantId,
            ISignalIngressSink ingress,
            IClock clock,
            AgentLogger logger,
            List<GatherRule> rules,
            string? imeLogPathOverride,
            bool unrestrictedMode = false)
        {
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            _logger = logger;
            _rules = rules ?? new List<GatherRule>();
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
}
