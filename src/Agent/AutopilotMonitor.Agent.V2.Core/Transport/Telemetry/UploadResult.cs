#nullable enable
using System;

namespace AutopilotMonitor.Agent.V2.Core.Transport.Telemetry
{
    /// <summary>
    /// Ergebnis eines Batch-Upload-Versuchs gegen das Backend. Plan §2.7a.
    /// </summary>
    public sealed class UploadResult
    {
        private UploadResult(bool success, string? errorReason, bool isTransient)
        {
            Success = success;
            ErrorReason = errorReason;
            IsTransient = isTransient;
        }

        public bool Success { get; }

        /// <summary>Null bei Success, gesetzt bei Fehler.</summary>
        public string? ErrorReason { get; }

        /// <summary>Bei Success ignoriert. Bei Fehler: true → Retry sinnvoll, false → dauerhafter Fehler (kein Retry).</summary>
        public bool IsTransient { get; }

        public static UploadResult Ok() => new UploadResult(true, null, isTransient: false);

        public static UploadResult Transient(string reason) =>
            new UploadResult(false, reason ?? throw new ArgumentNullException(nameof(reason)), isTransient: true);

        public static UploadResult Permanent(string reason) =>
            new UploadResult(false, reason ?? throw new ArgumentNullException(nameof(reason)), isTransient: false);
    }
}
