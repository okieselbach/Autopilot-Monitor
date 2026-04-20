using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// Serialises threading-sensitive test classes against each other so xUnit does not
    /// run them in parallel. Plan §4.x M4.5.c.
    /// <para>
    /// The affected tests use <see cref="System.Threading.SpinWait.SpinUntil"/> /
    /// <see cref="System.Threading.ManualResetEventSlim.Wait"/> with wall-clock timeouts.
    /// Under full-suite parallelism the ThreadPool gets contended by unrelated tests and
    /// those timeouts expire before the condition is observed — which we see as flaky
    /// "Clock_advance_past_throttle_window…", "Null_observer_is_supported…",
    /// "Reschedule_same_name_replaces_old_timer" and
    /// "RehydrateFromSnapshot_reschedules_each_deadline_past_due_fires_immediately" failures.
    /// </para>
    /// <para>
    /// Classes carrying <c>[Collection("SerialThreading")]</c> are guaranteed by xUnit to
    /// execute sequentially against each other (tests within a class are already sequential
    /// by default). Cost: ~2 extra seconds of wall-clock on the suite. Benefit: deterministic
    /// green runs in CI.
    /// </para>
    /// </summary>
    [CollectionDefinition("SerialThreading", DisableParallelization = true)]
    public sealed class SerialThreadingCollection
    {
        // Marker class — xUnit reads only the attribute. Intentionally empty.
    }
}
