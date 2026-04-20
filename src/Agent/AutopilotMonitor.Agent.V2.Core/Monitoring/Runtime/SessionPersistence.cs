using System;
using System.IO;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime
{
    /// <summary>
    /// Manages persistent session ID storage to maintain tracking across agent restarts
    /// </summary>
    public class SessionPersistence
    {
        /// <summary>
        /// Maximum age (hours) of a session.id file before the orphan guard treats it as
        /// stale and discards it. Matches <c>AgentConfiguration.AbsoluteMaxSessionHours</c>.
        /// A session.id younger than this whose session.created is missing is assumed to be
        /// from the current enrollment (file lost during reboot/crash) and will be recovered.
        /// </summary>
        internal const double OrphanGuardMaxAgeHours = 48;

        private readonly string _sessionFilePath;
        private readonly string _sequenceFilePath;
        private readonly string _whiteGloveMarkerPath;
        private readonly string _sessionCreatedPath;
        private readonly object _lockObject = new object();

        /// <summary>
        /// Protected constructor for testability (Moq proxy creation).
        /// </summary>
        protected SessionPersistence() { }

        public SessionPersistence(string dataDirectory)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory))
            {
                throw new ArgumentNullException(nameof(dataDirectory));
            }

            if (!Directory.Exists(dataDirectory))
            {
                Directory.CreateDirectory(dataDirectory);
            }

            _sessionFilePath = Path.Combine(dataDirectory, "session.id");
            _sequenceFilePath = Path.Combine(dataDirectory, "session.seq");
            _whiteGloveMarkerPath = Path.Combine(dataDirectory, "whiteglove.complete");
            _sessionCreatedPath = Path.Combine(dataDirectory, "session.created");
        }

        /// <summary>
        /// Loads existing session ID from disk or creates a new one if none exists
        /// </summary>
        /// <returns>Session ID (GUID string)</returns>
        public string LoadOrCreateSessionId()
        {
            lock (_lockObject)
            {
                try
                {
                    // Check if session file exists and is valid
                    if (File.Exists(_sessionFilePath))
                    {
                        // Guard: if session.id exists but session.created does not, check
                        // whether this is an orphaned session from a previous enrollment
                        // (ProgramData survived OS reset/reinstall) or a current session
                        // whose session.created was lost during a reboot/crash.
                        // Distinguish by file age: recent session.id (< OrphanGuardMaxAgeHours)
                        // is likely from the current enrollment → recover by initializing
                        // session.created and resuming. Old session.id is a true orphan → discard.
                        if (!File.Exists(_sessionCreatedPath))
                        {
                            var fileAge = DateTime.UtcNow - File.GetLastWriteTimeUtc(_sessionFilePath);
                            if (fileAge.TotalHours < OrphanGuardMaxAgeHours)
                            {
                                // Recent session — recover: initialize session.created and resume
                                var sessionId = File.ReadAllText(_sessionFilePath).Trim();
                                if (Guid.TryParse(sessionId, out _))
                                {
                                    SaveSessionCreatedAt(DateTime.UtcNow);
                                    return sessionId;
                                }
                            }

                            // Old session or invalid GUID — true orphan, discard
                            try { File.Delete(_sessionFilePath); } catch { }
                            try { File.Delete(_sequenceFilePath); } catch { }
                        }
                        else
                        {
                            var sessionId = File.ReadAllText(_sessionFilePath).Trim();

                            // Validate it's a valid GUID format
                            if (Guid.TryParse(sessionId, out _))
                            {
                                return sessionId;
                            }
                        }
                    }

                    // Create new session ID if file doesn't exist or is invalid
                    var newSessionId = Guid.NewGuid().ToString();
                    File.WriteAllText(_sessionFilePath, newSessionId);
                    SaveSessionCreatedAt(DateTime.UtcNow);
                    return newSessionId;
                }
                catch (Exception ex)
                {
                    // If we can't read/write the session file, generate a new ID in memory
                    // Log this error if logger is available
                    throw new InvalidOperationException($"Failed to load or create session ID at {_sessionFilePath}", ex);
                }
            }
        }

        /// <summary>
        /// Deletes the persisted session ID file
        /// This should be called when enrollment is complete
        /// </summary>
        public virtual void DeleteSession()
        {
            lock (_lockObject)
            {
                try
                {
                    if (File.Exists(_sessionFilePath))
                        File.Delete(_sessionFilePath);
                    if (File.Exists(_sequenceFilePath))
                        File.Delete(_sequenceFilePath);
                    if (File.Exists(_whiteGloveMarkerPath))
                        File.Delete(_whiteGloveMarkerPath);
                    if (File.Exists(_sessionCreatedPath))
                        File.Delete(_sessionCreatedPath);
                }
                catch (Exception)
                {
                    // Suppress deletion errors - not critical if cleanup fails
                    // The file will be overwritten on next start anyway
                }
            }
        }

        /// <summary>
        /// Checks if a persisted session ID exists
        /// </summary>
        /// <returns>True if session file exists</returns>
        public bool SessionExists()
        {
            lock (_lockObject)
            {
                return File.Exists(_sessionFilePath);
            }
        }

        /// <summary>
        /// Loads the persisted sequence counter. Returns 0 if no file exists or content is invalid.
        /// </summary>
        public long LoadSequence()
        {
            lock (_lockObject)
            {
                try
                {
                    if (File.Exists(_sequenceFilePath))
                    {
                        var content = File.ReadAllText(_sequenceFilePath).Trim();
                        if (long.TryParse(content, out var sequence) && sequence >= 0)
                            return sequence;
                    }
                }
                catch { }
                return 0;
            }
        }

        /// <summary>
        /// Persists the current sequence counter to disk.
        /// Called periodically and before graceful shutdown (WhiteGlove).
        /// </summary>
        public virtual void SaveSequence(long sequence)
        {
            lock (_lockObject)
            {
                try { File.WriteAllText(_sequenceFilePath, sequence.ToString()); }
                catch { } // Non-fatal: worst case, next boot starts with a small gap
            }
        }

        /// <summary>
        /// Deletes the persisted sequence file.
        /// Called alongside DeleteSession when enrollment completes.
        /// </summary>
        public void DeleteSequence()
        {
            lock (_lockObject)
            {
                try { if (File.Exists(_sequenceFilePath)) File.Delete(_sequenceFilePath); }
                catch { }
            }
        }

        /// <summary>
        /// Persists a marker indicating WhiteGlove Part 1 completed.
        /// On the next boot the agent reads this marker, emits a whiteglove_resumed
        /// event, and clears it — giving the backend an explicit signal to transition
        /// the session from Pending to InProgress for Part 2.
        /// </summary>
        public virtual void SaveWhiteGloveComplete()
        {
            lock (_lockObject)
            {
                try { File.WriteAllText(_whiteGloveMarkerPath, "1"); }
                catch { }
            }
        }

        /// <summary>
        /// Returns true if a WhiteGlove Part 1 marker exists, indicating this boot is Part 2.
        /// </summary>
        public bool IsWhiteGloveResume()
        {
            lock (_lockObject)
            {
                return File.Exists(_whiteGloveMarkerPath);
            }
        }

        /// <summary>
        /// Persists the session creation timestamp (UTC). Called once when a new session is created.
        /// </summary>
        public void SaveSessionCreatedAt(DateTime utcTimestamp)
        {
            lock (_lockObject)
            {
                try { File.WriteAllText(_sessionCreatedPath, utcTimestamp.ToString("O")); }
                catch { }
            }
        }

        /// <summary>
        /// Loads the persisted session creation timestamp. Returns null if file is missing or invalid.
        /// </summary>
        public DateTime? LoadSessionCreatedAt()
        {
            lock (_lockObject)
            {
                try
                {
                    if (File.Exists(_sessionCreatedPath))
                    {
                        var content = File.ReadAllText(_sessionCreatedPath).Trim();
                        if (DateTime.TryParse(content, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                            return dt.ToUniversalTime();
                    }
                }
                catch { }
                return null;
            }
        }

        /// <summary>
        /// Resets the session creation timestamp to now (UTC).
        /// Used on WhiteGlove Part 2 resume to give the user enrollment phase a fresh timer.
        /// </summary>
        public void ResetSessionCreatedAt()
        {
            SaveSessionCreatedAt(DateTime.UtcNow);
        }

        /// <summary>
        /// Removes the WhiteGlove marker after the whiteglove_resumed event has been emitted.
        /// </summary>
        public void ClearWhiteGloveComplete()
        {
            lock (_lockObject)
            {
                try { if (File.Exists(_whiteGloveMarkerPath)) File.Delete(_whiteGloveMarkerPath); }
                catch { }
            }
        }
    }
}
