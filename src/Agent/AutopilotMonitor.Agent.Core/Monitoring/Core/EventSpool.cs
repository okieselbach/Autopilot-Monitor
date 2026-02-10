using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.Core.Monitoring.Core
{
    /// <summary>
    /// Manages offline event storage (spool) for resilient uploads
    /// </summary>
    public class EventSpool : IDisposable
    {
        private readonly string _spoolDirectory;
        private readonly object _lockObject = new object();
        private readonly FileSystemWatcher _fileWatcher;

        /// <summary>
        /// Event raised when new events are added to the spool
        /// </summary>
        public event EventHandler EventsAvailable;

        public EventSpool(string spoolDirectory)
        {
            _spoolDirectory = spoolDirectory;

            if (!Directory.Exists(_spoolDirectory))
            {
                Directory.CreateDirectory(_spoolDirectory);
            }

            // Initialize FileSystemWatcher for efficient event detection
            _fileWatcher = new FileSystemWatcher(_spoolDirectory)
            {
                Filter = "event_*.json",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = false // Will be enabled when service starts
            };

            _fileWatcher.Created += OnFileCreated;
        }

        /// <summary>
        /// Starts watching for new events
        /// </summary>
        public void StartWatching()
        {
            _fileWatcher.EnableRaisingEvents = true;
        }

        /// <summary>
        /// Stops watching for new events
        /// </summary>
        public void StopWatching()
        {
            _fileWatcher.EnableRaisingEvents = false;
        }

        private void OnFileCreated(object sender, FileSystemEventArgs e)
        {
            // Raise event to trigger immediate upload
            EventsAvailable?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Adds an event to the spool
        /// </summary>
        public void Add(EnrollmentEvent evt)
        {
            lock (_lockObject)
            {
                var fileName = $"event_{evt.Timestamp:yyyyMMddHHmmssfff}_{evt.Sequence}.json";
                var filePath = Path.Combine(_spoolDirectory, fileName);

                var json = JsonConvert.SerializeObject(evt, Formatting.None);
                File.WriteAllText(filePath, json);
            }
        }

        /// <summary>
        /// Gets a batch of events from the spool (oldest first, sorted by sequence)
        /// </summary>
        public List<EnrollmentEvent> GetBatch(int maxBatchSize)
        {
            lock (_lockObject)
            {
                var events = new List<EnrollmentEvent>();
                var files = Directory.GetFiles(_spoolDirectory, "event_*.json")
                    .Select(f => new { Path = f, Sequence = ExtractSequenceFromFilename(f) })
                    .OrderBy(x => x.Sequence)
                    .Take(maxBatchSize)
                    .Select(x => x.Path)
                    .ToList();

                foreach (var file in files)
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var evt = JsonConvert.DeserializeObject<EnrollmentEvent>(json);
                        if (evt != null)
                        {
                            events.Add(evt);
                        }
                    }
                    catch
                    {
                        // Skip corrupted files
                    }
                }

                return events;
            }
        }

        /// <summary>
        /// Extracts the sequence number from event filename
        /// Filename format: event_{timestamp}_{sequence}.json
        /// </summary>
        private long ExtractSequenceFromFilename(string filePath)
        {
            try
            {
                var fileName = Path.GetFileNameWithoutExtension(filePath);
                var parts = fileName.Split('_');
                if (parts.Length >= 3)
                {
                    if (long.TryParse(parts[2], out var sequence))
                        return sequence;
                }
            }
            catch
            {
                // Fallback to 0 if parsing fails
            }
            return 0;
        }

        /// <summary>
        /// Removes events from the spool after successful upload
        /// </summary>
        public void RemoveEvents(List<EnrollmentEvent> events)
        {
            lock (_lockObject)
            {
                foreach (var evt in events)
                {
                    var pattern = $"event_*_{evt.Sequence}.json";
                    var files = Directory.GetFiles(_spoolDirectory, pattern);

                    foreach (var file in files)
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch
                        {
                            // Ignore deletion errors
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Gets the count of events in the spool
        /// </summary>
        public int GetCount()
        {
            lock (_lockObject)
            {
                return Directory.GetFiles(_spoolDirectory, "event_*.json").Length;
            }
        }

        /// <summary>
        /// Clears all events from the spool
        /// </summary>
        public void Clear()
        {
            lock (_lockObject)
            {
                var files = Directory.GetFiles(_spoolDirectory, "event_*.json");
                foreach (var file in files)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // Ignore deletion errors
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_fileWatcher != null)
            {
                _fileWatcher.EnableRaisingEvents = false;
                _fileWatcher.Created -= OnFileCreated;
                _fileWatcher.Dispose();
            }
        }
    }
}
