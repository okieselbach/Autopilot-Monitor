#nullable enable
using System;
using AutopilotMonitor.DecisionCore.Engine;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Ein einzelner Effect-Fehler. Plan §2.7b — wird vom Orchestrator (M4.4) als
    /// <c>DecisionTransition</c> mit <c>Trigger="effect_failure"</c> ins Journal geschrieben.
    /// </summary>
    public sealed class EffectFailure
    {
        public EffectFailure(DecisionEffectKind effectKind, string errorReason, bool isTransient, bool exhaustedRetries)
        {
            if (string.IsNullOrEmpty(errorReason))
            {
                throw new ArgumentException("ErrorReason is mandatory.", nameof(errorReason));
            }

            EffectKind = effectKind;
            ErrorReason = errorReason;
            IsTransient = isTransient;
            ExhaustedRetries = exhaustedRetries;
        }

        public DecisionEffectKind EffectKind { get; }
        public string ErrorReason { get; }
        public bool IsTransient { get; }
        public bool ExhaustedRetries { get; }
    }
}
