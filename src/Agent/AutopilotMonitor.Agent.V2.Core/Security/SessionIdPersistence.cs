using System;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Logging;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// Persists the current enrollment SessionId across agent restarts. Plan §4.x M4.5.b.
    /// <para>
    /// V2 uses a single <c>session.id</c> text file in the agent data directory — simpler than
    /// the Legacy <c>SessionPersistence</c> (no <c>.seq</c>, no white-glove marker, no session-age
    /// tracking; those live in the new DecisionState snapshot/journal).
    /// </para>
    /// <para>
    /// Write semantics are atomic (write-to-temp + <see cref="File.Replace(string,string,string)"/>) so a
    /// power-cut mid-write cannot leave a zero-byte file that later reads as "no session" and
    /// trips a fresh SessionId on recovery.
    /// </para>
    /// </summary>
    public sealed class SessionIdPersistence
    {
        private readonly string _sessionFilePath;
        private readonly object _lockObject = new object();

        public SessionIdPersistence(string dataDirectory)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory))
                throw new ArgumentNullException(nameof(dataDirectory));

            if (!Directory.Exists(dataDirectory))
                Directory.CreateDirectory(dataDirectory);

            _sessionFilePath = Path.Combine(dataDirectory, "session.id");
        }

        /// <summary>
        /// Returns the persisted SessionId or creates, writes and returns a fresh one.
        /// Thread-safe — concurrent callers see the same SessionId.
        /// </summary>
        public string GetOrCreate(AgentLogger logger = null)
        {
            lock (_lockObject)
            {
                try
                {
                    if (File.Exists(_sessionFilePath))
                    {
                        var existing = File.ReadAllText(_sessionFilePath).Trim();
                        if (Guid.TryParse(existing, out _))
                        {
                            logger?.Debug($"SessionIdPersistence: resumed existing SessionId={existing}.");
                            return existing;
                        }
                        logger?.Warning($"SessionIdPersistence: session.id contained non-GUID content — regenerating.");
                    }
                }
                catch (Exception ex)
                {
                    logger?.Warning($"SessionIdPersistence: failed to read session.id ({ex.Message}) — regenerating.");
                }

                var fresh = Guid.NewGuid().ToString();
                WriteAtomic(fresh, logger);
                logger?.Info($"SessionIdPersistence: created new SessionId={fresh}.");
                return fresh;
            }
        }

        /// <summary>Deletes the persisted SessionId. Next <see cref="GetOrCreate"/> starts a fresh session.</summary>
        public void Delete(AgentLogger logger = null)
        {
            lock (_lockObject)
            {
                try
                {
                    if (File.Exists(_sessionFilePath)) File.Delete(_sessionFilePath);
                }
                catch (Exception ex)
                {
                    logger?.Warning($"SessionIdPersistence: failed to delete session.id: {ex.Message}");
                }
            }
        }

        private void WriteAtomic(string sessionId, AgentLogger logger)
        {
            var tempPath = _sessionFilePath + ".tmp";
            try
            {
                File.WriteAllText(tempPath, sessionId);

                if (File.Exists(_sessionFilePath))
                {
                    // File.Replace preserves ACLs + is atomic on NTFS.
                    File.Replace(tempPath, _sessionFilePath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tempPath, _sessionFilePath);
                }
            }
            catch (Exception ex)
            {
                logger?.Error($"SessionIdPersistence: atomic write failed ({ex.Message}) — falling back to direct write.", ex);
                File.WriteAllText(_sessionFilePath, sessionId);
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
    }
}
