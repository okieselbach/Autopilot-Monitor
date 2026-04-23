using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Telemetry.Events;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Architecture
{
    /// <summary>
    /// Single-rail refactor plan §4.4 / §5.10 / §8 — enforcement gate on the
    /// <see cref="TelemetryEventEmitter"/> caller set. PR #10 promoted this test from a
    /// transitional baseline (3 dependents) to strict enforcement (exactly 2 permitted callers):
    /// <see cref="EventTimelineEmitter"/> (Rail A) and <see cref="BackPressureEventObserver"/>
    /// (documented meta exception — it cannot route through the ingress without creating a
    /// cycle because it observes the ingress itself).
    /// <para>
    /// <b>Scope</b>: this test inspects the V2.Core assembly only. The emitter class itself is
    /// <c>internal</c> post-PR #10, so there is no surface for the V2 executable assembly or
    /// any external library to reach it — the inventory is complete with the two types that
    /// live next to it in the <c>Telemetry.Events</c> namespace.
    /// </para>
    /// <para>
    /// <b>Detection strategy</b>: reflection over fields, constructor parameters, method
    /// parameters, and property return types. IL-level call-site detection is deliberately
    /// avoided — structural dependencies are sufficient to guarantee a type has any way to
    /// reach the emitter, and the reflection approach is cheap and dependency-free.
    /// </para>
    /// </summary>
    public sealed class TelemetryEventEmitterCallersBaselineTests
    {
        private static readonly Type EmitterType = typeof(TelemetryEventEmitter);

        /// <summary>
        /// The two and only two types permitted to reach <see cref="TelemetryEventEmitter"/>
        /// after the single-rail refactor. A third entry would mean someone reintroduced a
        /// bypass; the test fails and reviewers are forced to reason about whether the new
        /// caller belongs here (essentially never — the answer is almost always "route via
        /// ISignalIngressSink + InformationalEventPost instead").
        /// </summary>
        private static readonly HashSet<string> PermittedCallers = new HashSet<string>(StringComparer.Ordinal)
        {
            "AutopilotMonitor.Agent.V2.Core.Telemetry.Events.EventTimelineEmitter",
            "AutopilotMonitor.Agent.V2.Core.Telemetry.Events.BackPressureEventObserver",
        };

        [Fact]
        public void V2Core_types_with_structural_dependency_on_TelemetryEventEmitter_match_permitted_callers()
        {
            var actual = FindStructuralDependents(EmitterType.Assembly);

            Assert.Equal(PermittedCallers.OrderBy(x => x), actual.OrderBy(x => x));
        }

        private static ISet<string> FindStructuralDependents(Assembly assembly)
        {
            const BindingFlags AllMembers =
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.DeclaredOnly;

            var dependents = new HashSet<string>(StringComparer.Ordinal);

            foreach (var type in assembly.GetTypes())
            {
                if (type == EmitterType) continue;
                if (type.FullName == null) continue;
                // Compiler-generated display classes / iterators show up with '<' in the name; skip.
                if (type.FullName.Contains("<")) continue;

                if (HasFieldOfType(type, EmitterType, AllMembers)
                    || HasPropertyOfType(type, EmitterType, AllMembers)
                    || HasMethodOrCtorParameterOfType(type, EmitterType, AllMembers))
                {
                    dependents.Add(type.FullName);
                }
            }

            return dependents;
        }

        private static bool HasFieldOfType(Type type, Type target, BindingFlags flags) =>
            type.GetFields(flags).Any(f => f.FieldType == target);

        private static bool HasPropertyOfType(Type type, Type target, BindingFlags flags) =>
            type.GetProperties(flags).Any(p => p.PropertyType == target);

        private static bool HasMethodOrCtorParameterOfType(Type type, Type target, BindingFlags flags)
        {
            foreach (var method in type.GetMethods(flags))
            {
                if (method.GetParameters().Any(p => p.ParameterType == target)) return true;
            }
            foreach (var ctor in type.GetConstructors(flags))
            {
                if (ctor.GetParameters().Any(p => p.ParameterType == target)) return true;
            }
            return false;
        }
    }
}
