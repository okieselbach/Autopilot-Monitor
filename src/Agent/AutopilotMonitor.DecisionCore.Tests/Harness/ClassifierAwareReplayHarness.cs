using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.DecisionCore.Tests.Harness
{
    /// <summary>
    /// Replay harness that also executes <see cref="DecisionEffectKind.RunClassifier"/>
    /// effects, synthesizing the resulting <see cref="DecisionSignalKind.ClassifierVerdictIssued"/>
    /// signal and feeding it back into the reducer. Emulates the production EffectRunner
    /// for the pure-synchronous classifier case used in M3.3 / M3.4 (plan §2.4).
    /// <para>
    /// Anti-loop (plan §2.4): a classifier is skipped when the snapshot's
    /// <see cref="WhiteGloveSealingSnapshot.ComputeInputHash"/> matches the last
    /// <see cref="Hypothesis.LastClassifierVerdictId"/> recorded for that classifier — tested
    /// end-to-end by the Anti-Loop scenario.
    /// </para>
    /// </summary>
    public sealed class ClassifierAwareReplayHarness
    {
        private readonly IDecisionEngine _engine;
        private readonly IReadOnlyDictionary<string, IClassifier> _classifiers;

        public ClassifierAwareReplayHarness(
            IDecisionEngine engine,
            IReadOnlyDictionary<string, IClassifier> classifiers)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _classifiers = classifiers ?? throw new ArgumentNullException(nameof(classifiers));
        }

        public ReplayResult Replay(
            string sessionId,
            string tenantId,
            IReadOnlyList<DecisionSignal> signals)
        {
            if (signals == null) throw new ArgumentNullException(nameof(signals));

            var state = DecisionState.CreateInitial(sessionId, tenantId);
            var allTransitions = new List<DecisionTransition>(signals.Count * 2);
            ClassifierRunStats = new Dictionary<string, int>();

            long nextSyntheticOrdinal = long.MinValue; // reassigned once we know the max real ordinal.
            foreach (var s in signals)
            {
                if (s.SessionSignalOrdinal + 1 > nextSyntheticOrdinal)
                {
                    nextSyntheticOrdinal = s.SessionSignalOrdinal + 1;
                }
            }
            if (nextSyntheticOrdinal == long.MinValue) nextSyntheticOrdinal = 0;

            foreach (var signal in signals)
            {
                var step = _engine.Reduce(state, signal);
                state = step.NewState;
                allTransitions.Add(step.Transition);

                // Process effects: the only effect kind the harness acts on is RunClassifier;
                // scheduling / cancellation / event emission are bookkept by state + the
                // transition record.
                foreach (var effect in step.Effects)
                {
                    if (effect.Kind != DecisionEffectKind.RunClassifier) continue;
                    if (string.IsNullOrEmpty(effect.ClassifierId)) continue;
                    if (!_classifiers.TryGetValue(effect.ClassifierId!, out var classifier)) continue;

                    // Anti-loop: skip if snapshot hash matches the last verdict hash for this
                    // classifier. For M3.3 we only track WhiteGloveSealing on state, so the
                    // check is tight. Other classifiers will get their own state fields in M3.4+.
                    var snapshot = effect.ClassifierSnapshot
                        ?? throw new InvalidOperationException(
                            $"RunClassifier effect for '{effect.ClassifierId}' missing snapshot.");

                    var hashable = snapshot as IClassifierSnapshot;
                    var snapshotHash = hashable?.ComputeInputHash() ?? string.Empty;
                    var lastVerdictId = ClassifierVerdictLookup.LookupLastVerdictId(state, effect.ClassifierId!);
                    if (!string.IsNullOrEmpty(snapshotHash) &&
                        !string.IsNullOrEmpty(lastVerdictId) &&
                        snapshotHash == lastVerdictId)
                    {
                        Increment(effect.ClassifierId!, "skipped_by_antiloop");
                        continue;
                    }

                    var verdict = classifier.Classify(snapshot);
                    Increment(effect.ClassifierId!, "run");

                    var verdictSignal = new DecisionSignal(
                        sessionSignalOrdinal: nextSyntheticOrdinal,
                        sessionTraceOrdinal: nextSyntheticOrdinal,
                        kind: DecisionSignalKind.ClassifierVerdictIssued,
                        kindSchemaVersion: 1,
                        occurredAtUtc: signal.OccurredAtUtc,
                        sourceOrigin: $"harness:classifier:{effect.ClassifierId}",
                        evidence: new Evidence(
                            kind: EvidenceKind.Synthetic,
                            identifier: $"classifier:{effect.ClassifierId}:{verdict.InputHash}",
                            summary: $"{effect.ClassifierId}={verdict.Level}/{verdict.Score}"),
                        payload: new Dictionary<string, string>
                        {
                            ["classifier"] = verdict.ClassifierId,
                            ["level"] = verdict.Level.ToString(),
                            ["score"] = verdict.Score.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["reason"] = verdict.Reason,
                            ["inputHash"] = verdict.InputHash,
                        });
                    nextSyntheticOrdinal++;

                    var verdictStep = _engine.Reduce(state, verdictSignal);
                    state = verdictStep.NewState;
                    allTransitions.Add(verdictStep.Transition);
                }
            }

            return new ReplayResult(
                finalState: state,
                transitions: allTransitions,
                finalStepHash: ComputeStepHash(state));
        }

        /// <summary>Run counts per classifier (keys: "run", "skipped_by_antiloop"). For test assertions.</summary>
        public IDictionary<string, int> ClassifierRunStats { get; private set; } = new Dictionary<string, int>();

        private void Increment(string classifierId, string bucket)
        {
            var key = $"{classifierId}:{bucket}";
            ClassifierRunStats[key] = ClassifierRunStats.TryGetValue(key, out var v) ? v + 1 : 1;
        }

        private static string ComputeStepHash(DecisionState state)
        {
            var canonical = string.Join(
                "|",
                state.SessionId,
                state.TenantId,
                state.Stage.ToString(),
                state.Outcome?.ToString() ?? "null",
                state.StepIndex.ToString(),
                state.LastAppliedSignalOrdinal.ToString(),
                state.WhiteGloveSealing.Level.ToString(),
                state.WhiteGloveSealing.Score.ToString());

            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(canonical));
                var sb = new System.Text.StringBuilder(16);
                for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
