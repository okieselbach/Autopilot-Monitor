using System;
using System.IO;

namespace AutopilotMonitor.Agent.Core.Storage
{
    /// <summary>
    /// Manages persistent session ID storage to maintain tracking across agent restarts
    /// </summary>
    public class SessionPersistence
    {
        private readonly string _sessionFilePath;
        private readonly object _lockObject = new object();

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
                        var sessionId = File.ReadAllText(_sessionFilePath).Trim();

                        // Validate it's a valid GUID format
                        if (Guid.TryParse(sessionId, out _))
                        {
                            return sessionId;
                        }
                    }

                    // Create new session ID if file doesn't exist or is invalid
                    var newSessionId = Guid.NewGuid().ToString();
                    File.WriteAllText(_sessionFilePath, newSessionId);
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
        public void DeleteSession()
        {
            lock (_lockObject)
            {
                try
                {
                    if (File.Exists(_sessionFilePath))
                    {
                        File.Delete(_sessionFilePath);
                    }
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
    }
}
