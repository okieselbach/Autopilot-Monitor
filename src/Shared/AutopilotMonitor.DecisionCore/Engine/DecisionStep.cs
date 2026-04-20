using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.DecisionCore.Engine
{
    /// <summary>
    /// Return value of a single reducer call. Plan §2.3 contract:
    /// <c>(newState, transition, effects) = engine.Reduce(oldState, signal)</c>.
    /// </summary>
    public sealed class DecisionStep
    {
        public DecisionStep(
            DecisionState newState,
            DecisionTransition transition,
            IReadOnlyList<DecisionEffect> effects)
        {
            NewState = newState ?? throw new ArgumentNullException(nameof(newState));
            Transition = transition ?? throw new ArgumentNullException(nameof(transition));
            Effects = effects ?? throw new ArgumentNullException(nameof(effects));
        }

        public DecisionState NewState { get; }

        public DecisionTransition Transition { get; }

        public IReadOnlyList<DecisionEffect> Effects { get; }
    }
}
