using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.DecisionCore.Tests.Harness
{
    /// <summary>
    /// Replay harness — plan §4 M2.
    /// <para>
    /// Reads a JSONL stream of <see cref="DecisionSignal"/> records, feeds them through
    /// <see cref="IDecisionEngine.Reduce"/> sequentially, and computes a deterministic
    /// <see cref="ReplayResult.FinalStepHash"/> over the end state plus a terminal-stage
    /// assertion hook. M3 lights up the real reducer; M2 only needs the empty-stream path
    /// to run deterministically (gate requirement).
    /// </para>
    /// </summary>
    public sealed class ReplayHarness
    {
        private readonly IDecisionEngine _engine;

        public ReplayHarness(IDecisionEngine engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        public ReplayResult Replay(
            string sessionId,
            string tenantId,
            IReadOnlyList<DecisionSignal> signals)
        {
            if (signals == null)
            {
                throw new ArgumentNullException(nameof(signals));
            }

            var state = DecisionState.CreateInitial(sessionId, tenantId);
            var transitions = new List<DecisionTransition>(signals.Count);

            foreach (var signal in signals)
            {
                var step = _engine.Reduce(state, signal);
                state = step.NewState;
                transitions.Add(step.Transition);
            }

            return new ReplayResult(
                finalState: state,
                transitions: transitions,
                finalStepHash: ComputeStepHash(state));
        }

        /// <summary>
        /// Convenience for fixture files on disk. Each line is one <see cref="DecisionSignal"/>
        /// JSON record (comment lines starting with '#' and blank lines ignored).
        /// </summary>
        public ReplayResult ReplayFromJsonlFile(
            string path,
            string sessionId,
            string tenantId,
            Func<string, DecisionSignal> signalParser)
        {
            if (signalParser == null)
            {
                throw new ArgumentNullException(nameof(signalParser));
            }

            var signals = new List<DecisionSignal>();
            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }
                signals.Add(signalParser(line));
            }

            return Replay(sessionId, tenantId, signals);
        }

        /// <summary>
        /// Deterministic 16-hex-char SHA256 prefix over session-identifying terminal fields.
        /// Stable against irrelevant fields (SchemaVersion bump, etc.) by design — M3 will
        /// expand this once the reducer populates richer state; M2 only needs stability.
        /// </summary>
        private static string ComputeStepHash(DecisionState state)
        {
            var canonical = string.Join(
                "|",
                state.SessionId,
                state.TenantId,
                state.Stage.ToString(),
                state.Outcome?.ToString() ?? "null",
                state.StepIndex.ToString(),
                state.LastAppliedSignalOrdinal.ToString());

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++)
                {
                    sb.Append(bytes[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }
    }

    public sealed class ReplayResult
    {
        public ReplayResult(
            DecisionState finalState,
            IReadOnlyList<DecisionTransition> transitions,
            string finalStepHash)
        {
            FinalState = finalState;
            Transitions = transitions;
            FinalStepHash = finalStepHash;
        }

        public DecisionState FinalState { get; }

        public IReadOnlyList<DecisionTransition> Transitions { get; }

        public string FinalStepHash { get; }
    }
}
