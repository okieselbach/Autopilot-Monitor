#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    public sealed class RealmJoinDetectedEventArgs : EventArgs
    {
        public int DeploymentPhase { get; }
        public RealmJoinDetectedEventArgs(int deploymentPhase) { DeploymentPhase = deploymentPhase; }
    }

    public sealed class RealmJoinResolvedEventArgs : EventArgs
    {
        public int DeploymentPhase { get; }
        public RealmJoinResolvedEventArgs(int deploymentPhase) { DeploymentPhase = deploymentPhase; }
    }

    public sealed class RealmJoinPhaseChangedEventArgs : EventArgs
    {
        public int PreviousPhase { get; }
        public int CurrentPhase { get; }
        public RealmJoinPhaseChangedEventArgs(int previousPhase, int currentPhase)
        {
            PreviousPhase = previousPhase;
            CurrentPhase = currentPhase;
        }
    }

    public sealed class RealmJoinPackageEventArgs : EventArgs
    {
        public string Scope { get; }
        public string PackageId { get; }
        public string? DisplayName { get; }
        public string? Version { get; }
        public bool? Success { get; }
        public int? LastExitCode { get; }

        public RealmJoinPackageEventArgs(string scope, RealmJoinPackageSnapshot snapshot)
        {
            Scope = scope;
            PackageId = snapshot.PackageId;
            DisplayName = snapshot.DisplayName;
            Version = snapshot.Version;
            Success = snapshot.Success;
            LastExitCode = snapshot.LastExitCode;
        }
    }

    /// <summary>
    /// Owns the registry watchers that observe RealmJoin (RJ) deployment state. Three watchers
    /// in total — the Parameters key (DeploymentPhase) plus machine + user scope package
    /// roots. Each watcher attaches lazily: if the parent key does not exist yet, a 2 s retry
    /// timer probes until it appears. On every change the underlying values are re-read and the
    /// public events are fired with strict dedup semantics (Detected / Resolved fire once;
    /// PackageStarted / PackageCompleted fire once per <c>(scope, packageId)</c> pair).
    /// </summary>
    /// <remarks>
    /// The catch-up scan in <see cref="Start"/> reads the current values synchronously before
    /// attaching <see cref="RegistryWatcher"/>, so a session that boots while RJ is already at
    /// phase 110 (or with packages already installed) emits the matching one-shot events
    /// immediately instead of waiting for the next change notification.
    /// </remarks>
    internal sealed class RealmJoinWatcher : IDisposable
    {
        internal const int RetryIntervalSeconds = 2;
        internal const int DebounceMilliseconds = 500;

        private readonly AgentLogger _logger;
        private readonly object _stateLock = new object();

        private RegistryWatcher? _parametersWatcher;
        private RegistryWatcher? _hklmPackagesWatcher;
        private RegistryWatcher? _hkuPackagesWatcher;

        private Timer? _parametersRetryTimer;
        private Timer? _hklmPackagesRetryTimer;
        private Timer? _hkuPackagesRetryTimer;

        private Timer? _parametersDebounce;
        private Timer? _hklmPackagesDebounce;
        private Timer? _hkuPackagesDebounce;

        private bool _detectedFired;
        private bool _resolvedFired;
        private int? _lastPhase;

        private readonly HashSet<string> _hklmPackagesStarted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _hklmPackagesCompleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _hkuPackagesStarted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _hkuPackagesCompleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private string? _hkuSid;
        private bool _disposed;

        public event EventHandler<RealmJoinDetectedEventArgs>? RealmJoinDetected;
        public event EventHandler<RealmJoinResolvedEventArgs>? RealmJoinResolved;
        public event EventHandler<RealmJoinPhaseChangedEventArgs>? RealmJoinPhaseChanged;
        public event EventHandler<RealmJoinPackageEventArgs>? RealmJoinPackageStarted;
        public event EventHandler<RealmJoinPackageEventArgs>? RealmJoinPackageCompleted;

        public RealmJoinWatcher(AgentLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RealmJoinWatcher));

            // Initial catch-up reads. These fire any one-shot events for state that already
            // exists at agent boot (RJ pre-installed, packages already complete).
            CheckParameters();
            CheckMachinePackages();

            if (!TryStartParametersWatcher())
            {
                _logger.Info("RealmJoinWatcher: Parameters key not yet present — retrying every 2s");
                _parametersRetryTimer = new Timer(
                    _ => { if (TryStartParametersWatcher()) { _parametersRetryTimer?.Dispose(); _parametersRetryTimer = null; } },
                    null,
                    TimeSpan.FromSeconds(RetryIntervalSeconds),
                    TimeSpan.FromSeconds(RetryIntervalSeconds));
            }

            if (!TryStartMachinePackagesWatcher())
            {
                _logger.Info("RealmJoinWatcher: HKLM packages key not yet present — retrying every 2s");
                _hklmPackagesRetryTimer = new Timer(
                    _ => { if (TryStartMachinePackagesWatcher()) { _hklmPackagesRetryTimer?.Dispose(); _hklmPackagesRetryTimer = null; } },
                    null,
                    TimeSpan.FromSeconds(RetryIntervalSeconds),
                    TimeSpan.FromSeconds(RetryIntervalSeconds));
            }
        }

        /// <summary>
        /// Attach the user-scope (HKU\&lt;sid&gt;\SOFTWARE\RealmJoin\Packages) watcher. Called
        /// by the host once <see cref="DesktopArrivalDetector"/> resolves a real user and
        /// <see cref="UserSidResolver"/> produces the SID. Idempotent — second invocations with
        /// any SID are no-ops (we only track the first observed real user).
        /// </summary>
        public void ArmHku(string sid)
        {
            if (_disposed) return;
            if (string.IsNullOrEmpty(sid)) return;

            lock (_stateLock)
            {
                if (_hkuSid != null) return;
                _hkuSid = sid;
            }

            _logger.Info($"RealmJoinWatcher: arming HKU watcher for SID {sid}");

            CheckUserPackages();

            if (!TryStartUserPackagesWatcher())
            {
                _logger.Info("RealmJoinWatcher: HKU user-hive not yet loaded — retrying every 2s");
                _hkuPackagesRetryTimer = new Timer(
                    _ => { if (TryStartUserPackagesWatcher()) { _hkuPackagesRetryTimer?.Dispose(); _hkuPackagesRetryTimer = null; } },
                    null,
                    TimeSpan.FromSeconds(RetryIntervalSeconds),
                    TimeSpan.FromSeconds(RetryIntervalSeconds));
            }
        }

        public void Stop(string reason = "watcher_stopped")
        {
            try
            {
                lock (_stateLock)
                {
                    _parametersRetryTimer?.Dispose(); _parametersRetryTimer = null;
                    _hklmPackagesRetryTimer?.Dispose(); _hklmPackagesRetryTimer = null;
                    _hkuPackagesRetryTimer?.Dispose(); _hkuPackagesRetryTimer = null;

                    _parametersDebounce?.Dispose(); _parametersDebounce = null;
                    _hklmPackagesDebounce?.Dispose(); _hklmPackagesDebounce = null;
                    _hkuPackagesDebounce?.Dispose(); _hkuPackagesDebounce = null;

                    if (_parametersWatcher != null) { _parametersWatcher.Dispose(); _parametersWatcher = null; }
                    if (_hklmPackagesWatcher != null) { _hklmPackagesWatcher.Dispose(); _hklmPackagesWatcher = null; }
                    if (_hkuPackagesWatcher != null) { _hkuPackagesWatcher.Dispose(); _hkuPackagesWatcher = null; }
                }
                _logger.Info($"RealmJoinWatcher: stopped ({reason})");
            }
            catch (Exception ex)
            {
                _logger.Error("RealmJoinWatcher: error during Stop", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop("disposed");
        }

        // ---- watcher attach ---------------------------------------------------------------

        private bool TryStartParametersWatcher()
        {
            try
            {
                using (var probe = Registry.LocalMachine.OpenSubKey(RealmJoinInfo.ServiceRegistryPath))
                {
                    if (probe == null) return false;
                }
                lock (_stateLock)
                {
                    if (_parametersWatcher != null) return true;

                    _parametersDebounce = new Timer(_ => CheckParameters(), null, Timeout.Infinite, Timeout.Infinite);
                    _parametersWatcher = new RegistryWatcher(
                        RegistryHive.LocalMachine,
                        RealmJoinInfo.ServiceRegistryPath,
                        watchSubtree: false,
                        filter: RegistryNativeMethods.RegChangeNotifyFilter.LastSet,
                        trace: msg => _logger.Trace($"RealmJoinWatcher(params): {msg}"));
                    _parametersWatcher.Changed += (s, e) => _parametersDebounce?.Change(DebounceMilliseconds, Timeout.Infinite);
                    _parametersWatcher.Error += (s, ex) => _logger.Warning($"RealmJoinWatcher(params): {ex.Message}");
                    _parametersWatcher.Start();
                }
                _logger.Info("RealmJoinWatcher: attached to RJ Parameters");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warning($"RealmJoinWatcher: failed to start parameters watcher: {ex.Message}");
                return false;
            }
        }

        private bool TryStartMachinePackagesWatcher()
        {
            try
            {
                using (var probe = Registry.LocalMachine.OpenSubKey(RealmJoinInfo.MachinePackagesRegistryPath))
                {
                    if (probe == null) return false;
                }
                lock (_stateLock)
                {
                    if (_hklmPackagesWatcher != null) return true;

                    _hklmPackagesDebounce = new Timer(_ => CheckMachinePackages(), null, Timeout.Infinite, Timeout.Infinite);
                    _hklmPackagesWatcher = new RegistryWatcher(
                        RegistryHive.LocalMachine,
                        RealmJoinInfo.MachinePackagesRegistryPath,
                        watchSubtree: true,
                        filter: RegistryNativeMethods.RegChangeNotifyFilter.LastSet
                              | RegistryNativeMethods.RegChangeNotifyFilter.Name,
                        trace: msg => _logger.Trace($"RealmJoinWatcher(hklmPkg): {msg}"));
                    _hklmPackagesWatcher.Changed += (s, e) => _hklmPackagesDebounce?.Change(DebounceMilliseconds, Timeout.Infinite);
                    _hklmPackagesWatcher.Error += (s, ex) => _logger.Warning($"RealmJoinWatcher(hklmPkg): {ex.Message}");
                    _hklmPackagesWatcher.Start();
                }
                _logger.Info("RealmJoinWatcher: attached to HKLM packages root");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warning($"RealmJoinWatcher: failed to start HKLM packages watcher: {ex.Message}");
                return false;
            }
        }

        private bool TryStartUserPackagesWatcher()
        {
            string? sid;
            lock (_stateLock) { sid = _hkuSid; }
            if (string.IsNullOrEmpty(sid)) return false;

            var path = $"{sid}\\{RealmJoinInfo.UserPackagesRegistrySubPath}";
            try
            {
                using (var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default))
                using (var probe = users.OpenSubKey(path))
                {
                    if (probe == null) return false;
                }
                lock (_stateLock)
                {
                    if (_hkuPackagesWatcher != null) return true;

                    _hkuPackagesDebounce = new Timer(_ => CheckUserPackages(), null, Timeout.Infinite, Timeout.Infinite);
                    _hkuPackagesWatcher = new RegistryWatcher(
                        RegistryHive.Users,
                        path,
                        watchSubtree: true,
                        filter: RegistryNativeMethods.RegChangeNotifyFilter.LastSet
                              | RegistryNativeMethods.RegChangeNotifyFilter.Name,
                        trace: msg => _logger.Trace($"RealmJoinWatcher(hkuPkg): {msg}"));
                    _hkuPackagesWatcher.Changed += (s, e) => _hkuPackagesDebounce?.Change(DebounceMilliseconds, Timeout.Infinite);
                    _hkuPackagesWatcher.Error += (s, ex) => _logger.Warning($"RealmJoinWatcher(hkuPkg): {ex.Message}");
                    _hkuPackagesWatcher.Start();
                }
                _logger.Info($"RealmJoinWatcher: attached to HKU\\{sid} packages root");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warning($"RealmJoinWatcher: failed to start HKU packages watcher: {ex.Message}");
                return false;
            }
        }

        // ---- check / emit ------------------------------------------------------------------

        private void CheckParameters()
        {
            try
            {
                var phase = RealmJoinInfo.TryReadDeploymentPhase(RegistryHive.LocalMachine);
                if (phase == null) return;

                bool fireDetected;
                bool firePhaseChange;
                bool fireResolved;
                int? prevPhase;
                lock (_stateLock)
                {
                    fireDetected = !_detectedFired;
                    _detectedFired = true;
                    firePhaseChange = _lastPhase.HasValue && _lastPhase.Value != phase.Value;
                    prevPhase = _lastPhase;
                    _lastPhase = phase;
                    fireResolved = !_resolvedFired && phase.Value == RealmJoinInfo.PhaseCompletedFirstDeployment;
                    if (fireResolved) _resolvedFired = true;
                }

                if (fireDetected)
                {
                    _logger.Info($"RealmJoinWatcher: RJ detected (phase={phase.Value})");
                    try { RealmJoinDetected?.Invoke(this, new RealmJoinDetectedEventArgs(phase.Value)); }
                    catch (Exception ex) { _logger.Error("RealmJoinWatcher: RealmJoinDetected handler threw", ex); }
                }

                if (firePhaseChange && prevPhase.HasValue)
                {
                    try { RealmJoinPhaseChanged?.Invoke(this, new RealmJoinPhaseChangedEventArgs(prevPhase.Value, phase.Value)); }
                    catch (Exception ex) { _logger.Error("RealmJoinWatcher: RealmJoinPhaseChanged handler threw", ex); }
                }

                if (fireResolved)
                {
                    _logger.Info($"RealmJoinWatcher: RJ resolved (phase={phase.Value})");
                    try { RealmJoinResolved?.Invoke(this, new RealmJoinResolvedEventArgs(phase.Value)); }
                    catch (Exception ex) { _logger.Error("RealmJoinWatcher: RealmJoinResolved handler threw", ex); }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"RealmJoinWatcher: CheckParameters threw: {ex.Message}");
            }
        }

        private void CheckMachinePackages()
        {
            CheckPackages(
                scope: "machine",
                hive: RegistryHive.LocalMachine,
                packagesPath: RealmJoinInfo.MachinePackagesRegistryPath,
                startedSet: _hklmPackagesStarted,
                completedSet: _hklmPackagesCompleted);
        }

        private void CheckUserPackages()
        {
            string? sid;
            lock (_stateLock) { sid = _hkuSid; }
            if (string.IsNullOrEmpty(sid)) return;
            var path = $"{sid}\\{RealmJoinInfo.UserPackagesRegistrySubPath}";
            CheckPackages(
                scope: "user",
                hive: RegistryHive.Users,
                packagesPath: path,
                startedSet: _hkuPackagesStarted,
                completedSet: _hkuPackagesCompleted);
        }

        private void CheckPackages(
            string scope,
            RegistryHive hive,
            string packagesPath,
            HashSet<string> startedSet,
            HashSet<string> completedSet)
        {
            try
            {
                var ids = RealmJoinInfo.EnumeratePackageIds(hive, packagesPath);
                foreach (var id in ids)
                {
                    if (!RealmJoinInfo.TryReadPackage(hive, packagesPath, id, out var snap)) continue;
                    MaybeFirePackageEvents(scope, snap, startedSet, completedSet);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"RealmJoinWatcher: CheckPackages({scope}) threw: {ex.Message}");
            }
        }

        private void MaybeFirePackageEvents(
            string scope,
            RealmJoinPackageSnapshot snap,
            HashSet<string> startedSet,
            HashSet<string> completedSet)
        {
            bool fireStarted = false;
            bool fireCompleted = false;
            lock (_stateLock)
            {
                if (snap.HasStartedMarker && !startedSet.Contains(snap.PackageId))
                {
                    startedSet.Add(snap.PackageId);
                    fireStarted = true;
                }
                if (snap.HasCompletionMarker && !completedSet.Contains(snap.PackageId))
                {
                    completedSet.Add(snap.PackageId);
                    fireCompleted = true;
                }
            }

            if (fireStarted)
            {
                _logger.Info($"RealmJoinWatcher: package started (scope={scope}, id={snap.PackageId})");
                try { RealmJoinPackageStarted?.Invoke(this, new RealmJoinPackageEventArgs(scope, snap)); }
                catch (Exception ex) { _logger.Error("RealmJoinWatcher: RealmJoinPackageStarted handler threw", ex); }
            }

            if (fireCompleted)
            {
                _logger.Info($"RealmJoinWatcher: package completed (scope={scope}, id={snap.PackageId}, success={snap.Success}, exitCode={snap.LastExitCode})");
                try { RealmJoinPackageCompleted?.Invoke(this, new RealmJoinPackageEventArgs(scope, snap)); }
                catch (Exception ex) { _logger.Error("RealmJoinWatcher: RealmJoinPackageCompleted handler threw", ex); }
            }
        }

        // Test seam (used by V2.Core.Tests to drive deterministic emit paths without touching
        // the real registry).
        internal void TriggerCheckParametersFromTest() => CheckParameters();
        internal void TriggerCheckMachinePackagesFromTest() => CheckMachinePackages();
        internal void TriggerCheckUserPackagesFromTest() => CheckUserPackages();
    }
}
