#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Office
{
    /// <summary>
    /// Event-driven completion proof for an Office C2R install: watches the C2R install tree for any of
    /// the core Office app binaries (<c>WINWORD.EXE</c> / <c>EXCEL.EXE</c> / <c>POWERPNT.EXE</c> /
    /// <c>OUTLOOK.EXE</c>) appearing on disk. C2R lays these down in the integrate phase — their
    /// presence is the one reliable "Office is installed" signal (the DO job aggregate is unreliable for
    /// completion: multi-job churn never reaches an aggregate 100% and Connected-Cache delivery makes the
    /// stream near-instant — field session 7da7dead).
    /// <para>
    /// <b>Any-of</b> by design: a deployment can exclude products (e.g. Word only, no Outlook), so the
    /// first of the four binaries to appear is enough. The watcher fires <see cref="BinaryAppeared"/>
    /// exactly once. It is armed only once the <c>InstallationPath</c> is known from the registry (so we
    /// watch the correct tree). On a clean enrollment the binaries are absent at arm time and the
    /// FileSystemWatcher catches their creation during integrate; an initial scan also completes
    /// immediately if they are already present (armed late / update-over-existing).
    /// </para>
    /// Fail-soft: any failure is logged and the watcher stays quiet (no false completion).
    /// </summary>
    public sealed class OfficeBinaryWatcher : IDisposable
    {
        // C2R lays the binaries down under {InstallationPath}\root\OfficeNN\ — the version folder is
        // enumerated (IncludeSubdirectories), never hardcoded.
        private const int ArmRetryDelaySeconds = 5;
        private const int MaxArmRetries = 24; // ~2 min — the InstallationPath dir is normally already present

        private readonly string _installationPath;
        private readonly HashSet<string> _binaries; // upper-cased leaf names
        private readonly AgentLogger _logger;
        private readonly object _lock = new object();

        private FileSystemWatcher? _fsw;
        private Timer? _armRetryTimer;
        private int _armAttempts;
        private int _fired;   // 0/1 — BinaryAppeared raised at most once
        private bool _disposed;

        /// <summary>Raised once when a core Office binary is present on disk (install complete).</summary>
        public event EventHandler? BinaryAppeared;

        public OfficeBinaryWatcher(string installationPath, IEnumerable<string> binaries, AgentLogger logger)
        {
            _installationPath = installationPath ?? throw new ArgumentNullException(nameof(installationPath));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _binaries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in binaries ?? Array.Empty<string>()) _binaries.Add(b);
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_disposed) return;
                TryArm();
            }
        }

        // Caller holds _lock.
        private void TryArm()
        {
            if (_disposed || _fired != 0 || _fsw != null) return;

            // Initial scan — the binaries may already be present (armed late / update over existing).
            if (CoreBinariesPresent())
            {
                RaiseOnce();
                return;
            }

            if (!Directory.Exists(_installationPath))
            {
                // The install root is not there yet (very early). Retry a bounded number of times; the
                // registry InstallationPath value normally precedes the directory only briefly.
                if (_armAttempts++ < MaxArmRetries)
                {
                    _armRetryTimer?.Dispose();
                    _armRetryTimer = new Timer(OnArmRetry, null, TimeSpan.FromSeconds(ArmRetryDelaySeconds), Timeout.InfiniteTimeSpan);
                }
                else
                {
                    _logger.Debug($"[OfficeBinaryWatcher] install path '{_installationPath}' never appeared — giving up (no completion proof)");
                }
                return;
            }

            try
            {
                _fsw = new FileSystemWatcher(_installationPath)
                {
                    IncludeSubdirectories = true,
                    Filter = "*.exe",
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                _fsw.Created += OnFileEvent;
                _fsw.Changed += OnFileEvent;
                _fsw.Renamed += OnFileRenamed;
                _fsw.EnableRaisingEvents = true;
                _logger.Info($"[OfficeBinaryWatcher] watching '{_installationPath}' for core Office binaries");

                // Race: a binary may have appeared between the scan above and arming — re-scan once.
                if (CoreBinariesPresent()) RaiseOnce();
            }
            catch (Exception ex)
            {
                _logger.Warning($"[OfficeBinaryWatcher] could not watch '{_installationPath}': {ex.Message}");
            }
        }

        private void OnArmRetry(object? state)
        {
            lock (_lock) { TryArm(); }
        }

        private void OnFileEvent(object sender, FileSystemEventArgs e) => CheckCandidate(e.Name);
        private void OnFileRenamed(object sender, RenamedEventArgs e) => CheckCandidate(e.Name);

        private void CheckCandidate(string? relativeName)
        {
            try
            {
                if (string.IsNullOrEmpty(relativeName)) return;
                var leaf = Path.GetFileName(relativeName);
                if (!string.IsNullOrEmpty(leaf) && _binaries.Contains(leaf))
                {
                    _logger.Info($"[OfficeBinaryWatcher] core Office binary appeared: {leaf}");
                    RaiseOnce();
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"[OfficeBinaryWatcher] candidate check error: {ex.Message}");
            }
        }

        private bool CoreBinariesPresent()
            => OfficeInstallDetector.CoreBinariesPresentOnDisk(_installationPath, _logger);

        private void RaiseOnce()
        {
            if (Interlocked.Exchange(ref _fired, 1) != 0) return;
            try { BinaryAppeared?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _logger.Warning($"[OfficeBinaryWatcher] BinaryAppeared handler threw: {ex.Message}"); }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _armRetryTimer?.Dispose();
                _armRetryTimer = null;
                if (_fsw != null)
                {
                    try { _fsw.EnableRaisingEvents = false; } catch { }
                    try { _fsw.Created -= OnFileEvent; } catch { }
                    try { _fsw.Changed -= OnFileEvent; } catch { }
                    try { _fsw.Renamed -= OnFileRenamed; } catch { }
                    try { _fsw.Dispose(); } catch { }
                    _fsw = null;
                }
            }
        }
    }
}
