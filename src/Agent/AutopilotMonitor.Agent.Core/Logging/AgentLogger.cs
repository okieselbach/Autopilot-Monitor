using System;
using System.IO;

namespace AutopilotMonitor.Agent.Core.Logging
{
    /// <summary>
    /// Simple file-based logger for the agent
    /// </summary>
    public class AgentLogger
    {
        private readonly string _logFilePath;
        private readonly bool _enableDebug;
        private readonly object _lockObject = new object();

        public AgentLogger(string logDirectory, bool enableDebug = false)
        {
            _enableDebug = enableDebug;

            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }

            var logFileName = $"agent_{DateTime.Now:yyyyMMdd}.log";
            _logFilePath = Path.Combine(logDirectory, logFileName);
        }

        public void Debug(string message)
        {
            if (_enableDebug)
            {
                Log("DEBUG", message);
            }
        }

        public void Info(string message)
        {
            Log("INFO", message);
        }

        public void Warning(string message)
        {
            Log("WARN", message);
        }

        public void Error(string message, Exception ex = null)
        {
            var fullMessage = ex != null ? $"{message} - {ex}" : message;
            Log("ERROR", fullMessage);
        }

        private void Log(string level, string message)
        {
            try
            {
                lock (_lockObject)
                {
                    var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    var logEntry = $"[{timestamp}] [{level}] {message}";

                    File.AppendAllText(_logFilePath, logEntry + Environment.NewLine);
                }
            }
            catch
            {
                // Swallow logging errors to prevent crashes
            }
        }
    }
}
