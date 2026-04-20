using System;
using System.IO;

namespace AutopilotMonitor.Agent.V2.Core.Logging
{
    /// <summary>
    /// Log verbosity levels for the agent.
    /// Info  = normal operational messages (default)
    /// Debug = includes DEBUG-tagged lines (component state, config, decisions)
    /// Verbose = includes VERBOSE-tagged lines (per-event details, hot-path tracing)
    /// </summary>
    public enum AgentLogLevel
    {
        Info = 0,
        Debug = 1,
        Verbose = 2,
        Trace = 3
    }

    /// <summary>
    /// Simple file-based logger for the agent.
    /// Optionally mirrors output to the console when EnableConsoleOutput is set.
    /// </summary>
    public class AgentLogger
    {
        private readonly string _logFilePath;
        private AgentLogLevel _logLevel;
        private readonly object _lockObject = new object();

        /// <summary>
        /// When true, log entries are also written to Console.Out (same format as the log file).
        /// Set via --console flag or programmatically.
        /// </summary>
        public bool EnableConsoleOutput { get; set; }

        public AgentLogger(string logDirectory, AgentLogLevel logLevel = AgentLogLevel.Info)
        {
            _logLevel = logLevel;

            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }

            var logFileName = $"agent_{DateTime.Now:yyyyMMdd}.log";
            _logFilePath = Path.Combine(logDirectory, logFileName);
        }

        /// <summary>
        /// Updates the active log level at runtime (e.g. after remote config is applied).
        /// </summary>
        public void SetLogLevel(AgentLogLevel level)
        {
            _logLevel = level;
        }

        public AgentLogLevel LogLevel => _logLevel;

        public void Trace(string message)
        {
            if (_logLevel >= AgentLogLevel.Trace)
                Log("TRACE", message);
        }

        public void Verbose(string message)
        {
            if (_logLevel >= AgentLogLevel.Verbose)
                Log("VERBOSE", message);
        }

        public void Debug(string message)
        {
            if (_logLevel >= AgentLogLevel.Debug)
                Log("DEBUG", message);
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
                    var logEntry = $"[{timestamp}] [{level}] {SanitizeLogMessage(message)}";

                    File.AppendAllText(_logFilePath, logEntry + Environment.NewLine);

                    if (EnableConsoleOutput)
                    {
                        try { Console.WriteLine(logEntry); } catch { }
                    }
                }
            }
            catch
            {
                // Swallow logging errors to prevent crashes
            }
        }

        /// <summary>
        /// Sanitizes log messages to prevent log injection attacks.
        /// Escapes control characters that could forge log entries.
        /// Redaction patterns can be added here later as needed.
        /// </summary>
        private static string SanitizeLogMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;

            // Escape newlines and carriage returns to prevent log injection
            message = message.Replace("\r", "\\r").Replace("\n", "\\n");

            return message;
        }
    }
}
