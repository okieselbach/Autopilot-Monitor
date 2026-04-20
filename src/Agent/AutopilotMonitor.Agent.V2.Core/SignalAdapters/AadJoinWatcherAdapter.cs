#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;

namespace AutopilotMonitor.Agent.V2.Core.SignalAdapters
{
    /// <summary>
    /// Adapter for <see cref="AadJoinWatcher"/> → <see cref="DecisionSignalKind.AadUserJoinedLate"/>
    /// (Part 1) or <see cref="DecisionSignalKind.UserAadSignInComplete"/> (Part 2). Plan §2.1a / §2.2.
    /// <para>
    /// Only the <c>AadUserJoined</c>-Event (real user, non-placeholder) ist Decision-relevant
    /// und wird als Signal emittiert. <c>PlaceholderUserDetected</c> ist diagnostisch
    /// (transienter Provisioning-Account) und wird aktuell nicht als Decision-Signal
    /// weitergereicht — bei Bedarf später als Event-Timeline-Entry emittierbar.
    /// </para>
    /// </summary>
    internal sealed class AadJoinWatcherAdapter : IDisposable
    {
        private readonly AadJoinWatcher _watcher;
        private readonly ISignalIngressSink _ingress;
        private readonly IClock _clock;
        private readonly bool _part2Mode;
        private bool _fired;

        public AadJoinWatcherAdapter(
            AadJoinWatcher watcher,
            ISignalIngressSink ingress,
            IClock clock,
            bool part2Mode = false)
        {
            _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
            _ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _part2Mode = part2Mode;

            _watcher.AadUserJoined += OnAadUserJoined;
        }

        public void Dispose()
        {
            _watcher.AadUserJoined -= OnAadUserJoined;
        }

        private void OnAadUserJoined(object sender, AadUserJoinedEventArgs e) => EmitInternal(e.UserEmail, e.Thumbprint);

        internal void TriggerFromTest(string userEmail, string thumbprint) => EmitInternal(userEmail, thumbprint);

        private void EmitInternal(string userEmail, string thumbprint)
        {
            if (_fired) return;
            _fired = true;

            var kind = _part2Mode ? DecisionSignalKind.UserAadSignInComplete : DecisionSignalKind.AadUserJoinedLate;

            var payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Don't log the full email in decision signals — user PII. Keep domain only
                // for audit trail; full email lives only in local agent logs if at all.
                ["userDomain"] = ExtractDomain(userEmail),
                ["hasThumbprint"] = string.IsNullOrEmpty(thumbprint) ? "false" : "true",
            };

            _ingress.Post(
                kind: kind,
                occurredAtUtc: _clock.UtcNow,
                sourceOrigin: "AadJoinWatcher",
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "aad-join-watcher-v1",
                    summary: _part2Mode
                        ? "Post-reboot AAD user sign-in detected (JoinInfo registry key)"
                        : "Late AAD user join detected (JoinInfo registry key)",
                    derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["registryKey"] = @"HKLM\SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo",
                    }),
                payload: payload);
        }

        private static string ExtractDomain(string email)
        {
            if (string.IsNullOrEmpty(email)) return "unknown";
            var at = email.IndexOf('@');
            return at >= 0 && at + 1 < email.Length ? email.Substring(at + 1) : "unknown";
        }
    }
}
