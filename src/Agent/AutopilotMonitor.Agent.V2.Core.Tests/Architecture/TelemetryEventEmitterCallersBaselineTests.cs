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
    /// Single-rail refactor plan §4.4 / §8 — baseline inventory of every type in
    /// <c>AutopilotMonitor.Agent.V2.Core</c> that can reach
    /// <see cref="TelemetryEventEmitter"/>. The test is a living document: as the migration PRs
    /// (#1 through #9) remove bypass paths one by one, the baseline shrinks. PR #10 tightens
    /// the expected set to only the two permitted callers — <see cref="EventTimelineEmitter"/>
    /// (Rail A) and <see cref="BackPressureEventObserver"/> (documented meta exception).
    /// <para>
    /// <b>Scope</b>: this test inspects the V2.Core assembly only. Program.cs lives in the
    /// V2 executable assembly and reaches the emitter via
    /// <see cref="EnrollmentOrchestrator.EventEmitter"/>; internalizing that property in PR #10
    /// removes the reach automatically. Until then Program.cs is tracked via the plan's §2
    /// inventory (B2–B6), not this test.
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
        /// Types permitted to reach <see cref="TelemetryEventEmitter"/> at the end of the
        /// single-rail refactor (PR #10 enforcement gate). Kept separate from the transitional
        /// baseline so that a single-line diff in PR #10 switches enforcement on:
        /// <c>Assert.Equal(PermittedCallers, ActualCallers)</c>.
        /// </summary>
        private static readonly HashSet<string> PermittedCallersAfterPr10 = new HashSet<string>(StringComparer.Ordinal)
        {
            "AutopilotMonitor.Agent.V2.Core.Telemetry.Events.EventTimelineEmitter",
            "AutopilotMonitor.Agent.V2.Core.Telemetry.Events.BackPressureEventObserver",
        };

        /// <summary>
        /// Current transitional baseline (PR #0). Every additional entry is a documented
        /// bypass that a later PR will remove. Adding a new bypass requires updating this list
        /// explicitly — the review signal is loud because tests fail.
        /// </summary>
        private static readonly HashSet<string> ExpectedCurrentBaseline = new HashSet<string>(StringComparer.Ordinal)
        {
            // Rail A — engine effect path. Permitted after PR #10.
            "AutopilotMonitor.Agent.V2.Core.Telemetry.Events.EventTimelineEmitter",
            // Meta exception — cannot route through the ingress without a cycle. Permitted after PR #10.
            "AutopilotMonitor.Agent.V2.Core.Telemetry.Events.BackPressureEventObserver",
            // Owns the field and exposes it via the public EventEmitter property (BuildTelemetryEventSink
            // factory + the ServerActionDispatcher emitEvent callback in Program.cs). Removed /
            // internalized in PR #10 once those bypasses are themselves migrated.
            "AutopilotMonitor.Agent.V2.Core.Orchestration.EnrollmentOrchestrator",
            // PR #2 (5.2) migrated StartupEnvironmentProbes to InformationalEventPost — baseline
            // shrunk by one. Next to fall: the ServerActionDispatcher / EnrollmentTerminationHandler
            // callbacks, Periodic collectors, Enrollment SystemSignals trackers, Gather rules,
            // Analyzers, DeviceInfoCollector (PR #3 – PR #9).
        };

        [Fact]
        public void V2Core_types_with_structural_dependency_on_TelemetryEventEmitter_match_current_baseline()
        {
            var actual = FindStructuralDependents(EmitterType.Assembly);

            Assert.Equal(ExpectedCurrentBaseline.OrderBy(x => x), actual.OrderBy(x => x));
        }

        [Fact]
        public void Current_baseline_is_a_superset_of_the_PR10_permitted_callers()
        {
            // Sanity guard: every permitted-after-PR10 caller must be present today, otherwise
            // we've accidentally removed Rail A or the meta exception.
            foreach (var permitted in PermittedCallersAfterPr10)
            {
                Assert.Contains(permitted, ExpectedCurrentBaseline);
            }
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
