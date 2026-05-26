using System;

namespace AutopilotMonitor.Functions.Services.Backup
{
    /// <summary>
    /// Deterministic, non-retryable failure raised by the backup/restore paths
    /// (Manifest missing, SHA mismatch, Blob ETag changed, auth-table block,
    /// confirmation-token typo, lease busy …). The HTTP layer maps these to a
    /// specific 4xx; the queue worker maps them to <c>JobState.BlockedTerminal</c>
    /// + <c>DeleteMessage</c> on first attempt (no retry, no poison).
    /// </summary>
    public sealed class BackupTerminalException : Exception
    {
        /// <summary>
        /// Stable short code suitable for the HTTP error envelope
        /// (<c>{ error: Code, message: Message }</c>). UI uses this to decide which
        /// banner to render (e.g. <c>CurrentRowChanged</c> → "open Preview again").
        /// </summary>
        public string Code { get; }

        public BackupTerminalException(string code, string message) : base(message)
        {
            Code = code;
        }

        public BackupTerminalException(string code, string message, Exception inner) : base(message, inner)
        {
            Code = code;
        }
    }
}
