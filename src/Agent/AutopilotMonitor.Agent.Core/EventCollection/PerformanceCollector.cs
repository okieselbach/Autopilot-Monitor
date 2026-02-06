using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Threading;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.EventCollection
{
    /// <summary>
    /// Collects system performance metrics (CPU, memory, disk) on a configurable interval
    /// Optional collector - toggled on/off via remote config
    /// </summary>
    public class PerformanceCollector : IDisposable
    {
        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly Action<EnrollmentEvent> _onEventCollected;
        private readonly int _intervalSeconds;
        private Timer _pollTimer;

        private PerformanceCounter _cpuCounter;
        private PerformanceCounter _diskQueueCounter;

        public PerformanceCollector(string sessionId, string tenantId, Action<EnrollmentEvent> onEventCollected,
            AgentLogger logger, int intervalSeconds = 60)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _onEventCollected = onEventCollected ?? throw new ArgumentNullException(nameof(onEventCollected));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _intervalSeconds = intervalSeconds;
        }

        public void Start()
        {
            _logger.Info($"Starting Performance collector (interval: {_intervalSeconds}s)");

            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _diskQueueCounter = new PerformanceCounter("PhysicalDisk", "Current Disk Queue Length", "_Total");

                // Initial read to prime the counters (first reading is always 0)
                _cpuCounter.NextValue();
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to initialize performance counters: {ex.Message}");
            }

            // Start with an initial delay, then poll on interval
            _pollTimer = new Timer(
                _ => CollectMetrics(),
                null,
                TimeSpan.FromSeconds(5), // Initial delay to let counters warm up
                TimeSpan.FromSeconds(_intervalSeconds)
            );
        }

        public void Stop()
        {
            _logger.Info("Stopping Performance collector");
            _pollTimer?.Dispose();
            _pollTimer = null;
        }

        private void CollectMetrics()
        {
            try
            {
                var data = new Dictionary<string, object>();

                // CPU usage
                try
                {
                    var cpuPercent = _cpuCounter?.NextValue() ?? 0;
                    data["cpu_percent"] = Math.Round(cpuPercent, 1);
                }
                catch (Exception ex)
                {
                    _logger.Debug($"CPU counter read failed: {ex.Message}");
                }

                // Memory info via WMI
                try
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT FreePhysicalMemory, TotalVisibleMemorySize FROM Win32_OperatingSystem"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            var freeKb = Convert.ToDouble(obj["FreePhysicalMemory"]);
                            var totalKb = Convert.ToDouble(obj["TotalVisibleMemorySize"]);
                            data["memory_available_mb"] = Math.Round(freeKb / 1024, 0);
                            data["memory_total_mb"] = Math.Round(totalKb / 1024, 0);
                            data["memory_used_percent"] = Math.Round((1 - freeKb / totalKb) * 100, 1);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Memory WMI query failed: {ex.Message}");
                }

                // Disk queue length
                try
                {
                    var diskQueue = _diskQueueCounter?.NextValue() ?? 0;
                    data["disk_queue_length"] = Math.Round(diskQueue, 1);
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Disk queue counter read failed: {ex.Message}");
                }

                // Disk free space on system drive
                try
                {
                    var systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
                    var driveInfo = new DriveInfo(systemDrive);
                    data["disk_free_gb"] = Math.Round(driveInfo.AvailableFreeSpace / (1024.0 * 1024 * 1024), 1);
                    data["disk_total_gb"] = Math.Round(driveInfo.TotalSize / (1024.0 * 1024 * 1024), 1);
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Disk space query failed: {ex.Message}");
                }

                if (data.Count > 0)
                {
                    _onEventCollected(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        Timestamp = DateTime.UtcNow,
                        EventType = "performance_snapshot",
                        Severity = EventSeverity.Debug,
                        Source = "PerformanceCollector",
                        Message = $"CPU: {(data.ContainsKey("cpu_percent") ? data["cpu_percent"] : "?")}%, " +
                                  $"Memory: {(data.ContainsKey("memory_used_percent") ? data["memory_used_percent"] : "?")}%, " +
                                  $"Disk Free: {(data.ContainsKey("disk_free_gb") ? data["disk_free_gb"] : "?")} GB",
                        Data = data
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Performance collection failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Stop();
            _cpuCounter?.Dispose();
            _diskQueueCounter?.Dispose();
        }
    }
}
