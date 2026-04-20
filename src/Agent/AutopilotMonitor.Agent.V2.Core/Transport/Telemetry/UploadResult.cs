#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Transport.Telemetry
{
    /// <summary>
    /// Ergebnis eines Batch-Upload-Versuchs gegen das Backend. Plan §2.7a + §4.x M4.6.ε.
    /// <para>
    /// <b>M4.6.ε</b>: Carries optional backend-to-agent control signals that Legacy embedded in
    /// <c>IngestEventsResponse</c>: <see cref="DeviceBlocked"/> / <see cref="UnblockAt"/>
    /// (non-terminal quarantine), <see cref="DeviceKillSignal"/> (terminal force-self-destruct),
    /// <see cref="AdminAction"/> (portal-side Mark-Failed / Mark-Succeeded) and the generic
    /// <see cref="Actions"/> list. Program.cs subscribes to the transport's response-hook and
    /// feeds these through the <c>ServerActionDispatcher</c> / DeviceBlocked pause path.
    /// </para>
    /// </summary>
    public sealed class UploadResult
    {
        private UploadResult(
            bool success,
            string? errorReason,
            bool isTransient,
            bool deviceBlocked,
            DateTime? unblockAt,
            bool deviceKillSignal,
            string? adminAction,
            IReadOnlyList<ServerAction>? actions)
        {
            Success = success;
            ErrorReason = errorReason;
            IsTransient = isTransient;
            DeviceBlocked = deviceBlocked;
            UnblockAt = unblockAt;
            DeviceKillSignal = deviceKillSignal;
            AdminAction = adminAction;
            Actions = actions;
        }

        public bool Success { get; }

        /// <summary>Null bei Success, gesetzt bei Fehler.</summary>
        public string? ErrorReason { get; }

        /// <summary>Bei Success ignoriert. Bei Fehler: true → Retry sinnvoll, false → dauerhafter Fehler (kein Retry).</summary>
        public bool IsTransient { get; }

        /// <summary>
        /// Non-terminal quarantine signalled by the backend. The agent should stop draining
        /// uploads until <see cref="UnblockAt"/> (if set) elapses — the session stays alive so
        /// the Admin can un-block without discarding enrollment state. Plan §4.x M4.6.ε.
        /// </summary>
        public bool DeviceBlocked { get; }

        /// <summary>UTC time at which the <see cref="DeviceBlocked"/> quarantine auto-expires, or null for an indefinite block.</summary>
        public DateTime? UnblockAt { get; }

        /// <summary>
        /// Terminal kill-signal issued by an administrator. Legacy synthesised a
        /// <c>terminate_session</c> <see cref="ServerAction"/> with <c>forceSelfDestruct=true</c>
        /// and <c>gracePeriodSeconds=0</c>. V2 Program.cs replicates that synthesis + dispatches
        /// through the same termination path as the real decision-terminal.
        /// </summary>
        public bool DeviceKillSignal { get; }

        /// <summary>
        /// Portal-side Mark-Failed / Mark-Succeeded outcome string (e.g. <c>"failed"</c> /
        /// <c>"succeeded"</c>). Legacy synthesised a soft <c>terminate_session</c>
        /// <see cref="ServerAction"/> that respects the local <c>SelfDestructOnComplete</c> toggle.
        /// </summary>
        public string? AdminAction { get; }

        /// <summary>Generic backend-queued actions (<see cref="ServerAction"/>s) for this batch.</summary>
        public IReadOnlyList<ServerAction>? Actions { get; }

        public static UploadResult Ok() =>
            new UploadResult(true, null, isTransient: false,
                deviceBlocked: false, unblockAt: null, deviceKillSignal: false, adminAction: null, actions: null);

        /// <summary>
        /// Success with backend-to-agent control signals. Used by
        /// <see cref="BackendTelemetryUploader"/> when the 2xx response body carries one of the
        /// quarantine / kill / admin / actions fields.
        /// </summary>
        public static UploadResult OkWithSignals(
            bool deviceBlocked = false,
            DateTime? unblockAt = null,
            bool deviceKillSignal = false,
            string? adminAction = null,
            IReadOnlyList<ServerAction>? actions = null) =>
            new UploadResult(true, null, isTransient: false,
                deviceBlocked: deviceBlocked, unblockAt: unblockAt,
                deviceKillSignal: deviceKillSignal, adminAction: adminAction, actions: actions);

        public static UploadResult Transient(string reason) =>
            new UploadResult(false, reason ?? throw new ArgumentNullException(nameof(reason)), isTransient: true,
                deviceBlocked: false, unblockAt: null, deviceKillSignal: false, adminAction: null, actions: null);

        public static UploadResult Permanent(string reason) =>
            new UploadResult(false, reason ?? throw new ArgumentNullException(nameof(reason)), isTransient: false,
                deviceBlocked: false, unblockAt: null, deviceKillSignal: false, adminAction: null, actions: null);
    }
}
