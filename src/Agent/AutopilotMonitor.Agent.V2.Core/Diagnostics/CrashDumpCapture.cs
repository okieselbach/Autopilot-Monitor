#nullable enable
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Diagnostics
{
    /// <summary>
    /// Registers handlers for <see cref="AppDomain.UnhandledException"/> and
    /// <see cref="TaskScheduler.UnobservedTaskException"/>. When fired, synchronously writes
    /// a MiniDump (via <see cref="MiniDumpWriter"/>) and a <see cref="CrashRecord"/> JSON sibling
    /// to <see cref="CrashesDirectory"/>. <see cref="PendingCrashReporter"/> picks them up on the
    /// next agent start and emits a <c>previous_crash_detected</c> timeline event.
    /// <para>
    /// All work is synchronous and fail-soft — the process is about to die so there's no
    /// async window. <see cref="StackOverflowException"/>, <c>SIGKILL</c> and OS force-terminate
    /// are accepted limitations (no dump possible).
    /// </para>
    /// </summary>
    public static class CrashDumpCapture
    {
        public const string CrashesDirectoryName = "Crashes";
        public const int MaxRetainedCrashes = 5;
        public static readonly TimeSpan MaxCrashAge = TimeSpan.FromDays(7);

        private static int _registered;
        private static string _crashesDir = string.Empty;
        private static string _sessionId = string.Empty;
        private static string _tenantId = string.Empty;
        private static string _agentVersion = string.Empty;

        /// <summary>
        /// Idempotent. Registers the global exception handlers. Subsequent calls update the
        /// captured session/tenant labels but do not re-register handlers.
        /// </summary>
        public static void RegisterHandlers(
            string programDataDirectory,
            string sessionId,
            string tenantId,
            string agentVersion)
        {
            _crashesDir = Path.Combine(programDataDirectory, CrashesDirectoryName);
            _sessionId = sessionId ?? string.Empty;
            _tenantId = tenantId ?? string.Empty;
            _agentVersion = agentVersion ?? string.Empty;

            if (Interlocked.Exchange(ref _registered, 1) == 1) return;

            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        /// <summary>Folder where crash artefacts are written. Empty until <see cref="RegisterHandlers"/> runs.</summary>
        public static string CrashesDirectory => _crashesDir;

        private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                if (e?.ExceptionObject is Exception ex)
                {
                    WriteCrashArtefacts(ex, trigger: "AppDomain.UnhandledException");
                }
            }
            catch { /* nowhere left to log */ }
        }

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                if (e?.Exception != null)
                {
                    WriteCrashArtefacts(e.Exception, trigger: "TaskScheduler.UnobservedTaskException");
                    e.SetObserved();
                }
            }
            catch { /* nowhere left to log */ }
        }

        private static void WriteCrashArtefacts(Exception ex, string trigger)
        {
            try
            {
                Directory.CreateDirectory(_crashesDir);
            }
            catch { return; }

            var timestamp = DateTime.UtcNow;
            var stem = $"{timestamp:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}".Substring(0, 23);
            var dumpPath = Path.Combine(_crashesDir, stem + ".dmp");
            var jsonPath = Path.Combine(_crashesDir, stem + ".json");

            bool dumpOk = MiniDumpWriter.TryWriteDump(dumpPath);
            long dumpSize = 0;
            if (dumpOk)
            {
                try { dumpSize = new FileInfo(dumpPath).Length; } catch { }
            }

            var record = new CrashRecord
            {
                CrashedAt = timestamp,
                SessionId = _sessionId,
                TenantId = _tenantId,
                AgentVersion = _agentVersion,
                Trigger = trigger,
                ExceptionType = ex.GetType().FullName,
                ExceptionMessage = Truncate(ex.Message, 2000),
                StackTrace = Truncate(ex.ToString(), 8000),
                DumpFilePath = dumpOk ? Path.GetFileName(dumpPath) : null,
                DumpFileSizeBytes = dumpOk ? dumpSize : (long?)null,
                DumpWriteSucceeded = dumpOk,
            };

            try
            {
                File.WriteAllText(jsonPath, JsonConvert.SerializeObject(record, Formatting.Indented));
            }
            catch { /* nowhere left to log */ }
        }

        private static string? Truncate(string? s, int maxLen) =>
            s == null ? null : (s.Length <= maxLen ? s : s.Substring(0, maxLen) + "…[truncated]");

        /// <summary>
        /// Apply retention policy: keep at most <see cref="MaxRetainedCrashes"/> dumps and delete
        /// anything older than <see cref="MaxCrashAge"/>. Called from <see cref="PendingCrashReporter"/>
        /// after it has emitted pending records.
        /// </summary>
        internal static void ApplyRetention(string crashesDir)
        {
            if (!Directory.Exists(crashesDir)) return;

            try
            {
                var cutoff = DateTime.UtcNow - MaxCrashAge;
                foreach (var f in Directory.GetFiles(crashesDir))
                {
                    try
                    {
                        if (File.GetCreationTimeUtc(f) < cutoff) File.Delete(f);
                    }
                    catch { /* keep going */ }
                }

                var dumps = Directory.GetFiles(crashesDir, "*.dmp");
                if (dumps.Length > MaxRetainedCrashes)
                {
                    Array.Sort(dumps, (a, b) => File.GetCreationTimeUtc(a).CompareTo(File.GetCreationTimeUtc(b)));
                    var toDelete = dumps.Length - MaxRetainedCrashes;
                    for (int i = 0; i < toDelete; i++)
                    {
                        try { File.Delete(dumps[i]); } catch { }
                        try { File.Delete(Path.ChangeExtension(dumps[i], ".json")); } catch { }
                    }
                }
            }
            catch { /* retention is best-effort */ }
        }
    }
}
