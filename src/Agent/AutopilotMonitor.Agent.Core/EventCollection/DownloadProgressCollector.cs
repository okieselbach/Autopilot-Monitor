using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.EventCollection
{
    /// <summary>
    /// Monitors IME content download directories to track app download progress
    /// Optional collector - toggled on/off via remote config
    /// </summary>
    public class DownloadProgressCollector : IDisposable
    {
        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly Action<EnrollmentEvent> _onEventCollected;
        private readonly int _intervalSeconds;
        private Timer _pollTimer;

        // Track file sizes for download rate calculation
        private readonly Dictionary<string, long> _previousFileSizes = new Dictionary<string, long>();
        private DateTime _lastCheck = DateTime.MinValue;

        // IME content directories
        private static readonly string[] MonitoredPaths = new[]
        {
            @"C:\Windows\IMECache",
            @"C:\Program Files (x86)\Microsoft Intune Management Extension\Content"
        };

        public DownloadProgressCollector(string sessionId, string tenantId, Action<EnrollmentEvent> onEventCollected,
            AgentLogger logger, int intervalSeconds = 15)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _onEventCollected = onEventCollected ?? throw new ArgumentNullException(nameof(onEventCollected));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _intervalSeconds = intervalSeconds;
        }

        public void Start()
        {
            _logger.Info($"Starting Download Progress collector (interval: {_intervalSeconds}s)");

            _pollTimer = new Timer(
                _ => CheckDownloads(),
                null,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(_intervalSeconds)
            );
        }

        public void Stop()
        {
            _logger.Info("Stopping Download Progress collector");
            _pollTimer?.Dispose();
            _pollTimer = null;
        }

        private void CheckDownloads()
        {
            try
            {
                var now = DateTime.UtcNow;
                var elapsedSeconds = _lastCheck == DateTime.MinValue ? _intervalSeconds : (now - _lastCheck).TotalSeconds;
                var currentFiles = new Dictionary<string, long>();
                var hasActiveDownloads = false;

                foreach (var basePath in MonitoredPaths)
                {
                    if (!Directory.Exists(basePath))
                        continue;

                    try
                    {
                        var files = Directory.GetFiles(basePath, "*", SearchOption.AllDirectories);
                        foreach (var filePath in files)
                        {
                            try
                            {
                                var info = new FileInfo(filePath);
                                var size = info.Length;
                                currentFiles[filePath] = size;

                                // Check if this file is actively being downloaded (size changed)
                                long previousSize;
                                if (_previousFileSizes.TryGetValue(filePath, out previousSize))
                                {
                                    if (size > previousSize)
                                    {
                                        hasActiveDownloads = true;
                                        var bytesDownloaded = size - previousSize;
                                        var downloadRateBps = elapsedSeconds > 0 ? bytesDownloaded / elapsedSeconds : 0;

                                        var data = new Dictionary<string, object>
                                        {
                                            { "file_name", Path.GetFileName(filePath) },
                                            { "directory", Path.GetDirectoryName(filePath) },
                                            { "bytes_downloaded", size },
                                            { "bytes_delta", bytesDownloaded },
                                            { "download_rate_bps", Math.Round(downloadRateBps, 0) },
                                            { "download_rate_mbps", Math.Round(downloadRateBps / (1024 * 1024), 2) }
                                        };

                                        _onEventCollected(new EnrollmentEvent
                                        {
                                            SessionId = _sessionId,
                                            TenantId = _tenantId,
                                            Timestamp = DateTime.UtcNow,
                                            EventType = "download_progress",
                                            Severity = EventSeverity.Debug,
                                            Source = "DownloadProgressCollector",
                                            Message = $"Download: {Path.GetFileName(filePath)} - {FormatBytes(size)} ({FormatBytes((long)downloadRateBps)}/s)",
                                            Data = data
                                        });
                                    }
                                }
                                else if (size > 0)
                                {
                                    // New file detected
                                    hasActiveDownloads = true;

                                    _onEventCollected(new EnrollmentEvent
                                    {
                                        SessionId = _sessionId,
                                        TenantId = _tenantId,
                                        Timestamp = DateTime.UtcNow,
                                        EventType = "download_progress",
                                        Severity = EventSeverity.Debug,
                                        Source = "DownloadProgressCollector",
                                        Message = $"Download started: {Path.GetFileName(filePath)} ({FormatBytes(size)})",
                                        Data = new Dictionary<string, object>
                                        {
                                            { "file_name", Path.GetFileName(filePath) },
                                            { "directory", Path.GetDirectoryName(filePath) },
                                            { "bytes_downloaded", size },
                                            { "status", "started" }
                                        }
                                    });
                                }
                            }
                            catch (Exception)
                            {
                                // File may be locked or in use - skip
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug($"Error scanning download directory {basePath}: {ex.Message}");
                    }
                }

                // Detect completed downloads (files that existed before but are no longer present)
                foreach (var kvp in _previousFileSizes)
                {
                    if (!currentFiles.ContainsKey(kvp.Key))
                    {
                        _onEventCollected(new EnrollmentEvent
                        {
                            SessionId = _sessionId,
                            TenantId = _tenantId,
                            Timestamp = DateTime.UtcNow,
                            EventType = "download_progress",
                            Severity = EventSeverity.Info,
                            Source = "DownloadProgressCollector",
                            Message = $"Download completed/removed: {Path.GetFileName(kvp.Key)} ({FormatBytes(kvp.Value)})",
                            Data = new Dictionary<string, object>
                            {
                                { "file_name", Path.GetFileName(kvp.Key) },
                                { "final_size", kvp.Value },
                                { "status", "completed" }
                            }
                        });
                    }
                }

                // Update tracked files
                _previousFileSizes.Clear();
                foreach (var kvp in currentFiles)
                {
                    _previousFileSizes[kvp.Key] = kvp.Value;
                }
                _lastCheck = now;
            }
            catch (Exception ex)
            {
                _logger.Warning($"Download progress check failed: {ex.Message}");
            }
        }

        private string FormatBytes(long bytes)
        {
            if (bytes >= 1073741824)
                return $"{bytes / 1073741824.0:F1} GB";
            if (bytes >= 1048576)
                return $"{bytes / 1048576.0:F1} MB";
            if (bytes >= 1024)
                return $"{bytes / 1024.0:F1} KB";
            return $"{bytes} B";
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
