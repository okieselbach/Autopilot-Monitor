using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;

namespace AutopilotMonitor.SummaryDialog
{
    public partial class App : Application
    {
        internal static string StatusFilePath { get; private set; }
        internal static int TimeoutSeconds { get; private set; } = 60;
        internal static string BrandingImageUrl { get; private set; }
        internal static bool? ForceTheme { get; private set; } // true=dark, false=light, null=auto

        private static string _logFile;
        private static string _tempLogFile; // error log in %TEMP% (survives self-cleanup)
        private static bool _hadErrors;

        public App()
        {
            // Set up crash logging as early as possible to diagnose launch failures
            // (e.g. when launched via CreateProcessAsUser from SYSTEM service)
            try
            {
                var exeDir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
                _logFile = Path.Combine(exeDir, "SummaryDialog.log");

                // Error log in user's temp folder — survives self-cleanup for troubleshooting
                try { _tempLogFile = Path.Combine(Path.GetTempPath(), "SummaryDialog-error.log"); }
                catch { /* temp path unavailable */ }

                Log("App constructor — process started");
                LogDiagnostics();
            }
            catch (Exception ex)
            {
                LogError($"App constructor failed: {ex}");
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                LogError($"UNHANDLED EXCEPTION: {e.ExceptionObject}");
            };

            DispatcherUnhandledException += (s, e) =>
            {
                LogError($"DISPATCHER EXCEPTION: {e.Exception}");
                e.Handled = true; // prevent uncontrolled crash, ensure OnExit runs
                try { Shutdown(1); } catch { }
            };
        }

        private void LogDiagnostics()
        {
            try
            {
                var proc = Process.GetCurrentProcess();
                Log($"PID={proc.Id} SessionId={proc.SessionId}");

                // Session 0 = SYSTEM session — window will NOT be visible to the user
                if (proc.SessionId == 0)
                    LogError("CRITICAL: Process is in Session 0 (SYSTEM) — window will NOT be visible!");

                Log($"User: {Environment.UserDomainName}\\{Environment.UserName}");
                Log($"CommandLine: {Environment.CommandLine}");
                Log($"ExeDir: {Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)}");
                Log($"Temp: {Path.GetTempPath()}");
                Log($"Is64Bit: {Environment.Is64BitProcess} OS: {Environment.OSVersion}");

                // Check if critical dependency exists
                var exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                var jsonDll = Path.Combine(exeDir ?? ".", "Newtonsoft.Json.dll");
                if (!File.Exists(jsonDll))
                    LogError($"Newtonsoft.Json.dll NOT FOUND at {jsonDll}");
            }
            catch (Exception ex)
            {
                LogError($"LogDiagnostics failed: {ex.Message}");
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            Log($"OnStartup — args: {string.Join(" ", e.Args)}");
            base.OnStartup(e);
            ParseArguments(e.Args);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log("OnExit — shutting down");
            base.OnExit(e);
            CleanupTempErrorLog();
            SelfCleanup();
        }

        private void ParseArguments(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--status-file":
                        if (i + 1 < args.Length) StatusFilePath = args[++i];
                        break;
                    case "--timeout":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out var timeout))
                            TimeoutSeconds = timeout;
                        break;
                    case "--branding-url":
                        if (i + 1 < args.Length) BrandingImageUrl = args[++i];
                        break;
                    case "--dark-theme":
                        ForceTheme = true;
                        break;
                    case "--light-theme":
                        ForceTheme = false;
                        break;
                }
            }

            if (string.IsNullOrEmpty(StatusFilePath) || !File.Exists(StatusFilePath))
            {
                // Still show dialog with a fallback message
            }
        }

        /// <summary>
        /// Self-cleanup: delete the staging folder this EXE was launched from.
        /// Uses PowerShell Wait-Process to cleanly wait for our process to exit,
        /// then deletes the folder. No polling loops or race conditions.
        /// </summary>
        private void SelfCleanup()
        {
            try
            {
                var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                var exeDir = Path.GetDirectoryName(exePath);

                // Only self-cleanup if running from a known staging folder
                if (exeDir == null)
                    return;

                var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                var summaryStaging = Path.Combine(programData, "AutopilotMonitor-Summary");
                var legacyStaging = Path.Combine(programData, "AutopilotMonitor");
                var tempRoot = Path.GetTempPath().TrimEnd('\\');

                var isStagedCopy = exeDir.StartsWith(summaryStaging, StringComparison.OrdinalIgnoreCase)
                                || exeDir.StartsWith(legacyStaging, StringComparison.OrdinalIgnoreCase)
                                || exeDir.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase);
                if (!isStagedCopy)
                    return;

                var pid = Process.GetCurrentProcess().Id;

                // PowerShell Wait-Process: waits for our process to actually exit (no polling),
                // then deletes the staging folder. 60s timeout as safety net.
                var psScript = $"Wait-Process -Id {pid} -Timeout 60 -ErrorAction SilentlyContinue; " +
                               $"Start-Sleep -Seconds 1; " +
                               $"Remove-Item -LiteralPath '{exeDir}' -Recurse -Force -ErrorAction SilentlyContinue";
                var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));

                Log($"SelfCleanup: spawning PowerShell to delete '{exeDir}' after PID {pid} exits");

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log($"SelfCleanup failed: {ex.Message}");
                // Best-effort cleanup, ignore errors
            }
        }

        /// <summary>
        /// Delete the temp error log if no errors occurred (no traces on success).
        /// </summary>
        private void CleanupTempErrorLog()
        {
            try
            {
                if (!_hadErrors && _tempLogFile != null && File.Exists(_tempLogFile))
                    File.Delete(_tempLogFile);
            }
            catch { }
        }

        internal static void Log(string message)
        {
            try
            {
                if (_logFile != null)
                    File.AppendAllText(_logFile,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch { }
        }

        /// <summary>
        /// Log an error to both the main log and the temp error log.
        /// The temp error log survives self-cleanup for post-mortem troubleshooting.
        /// </summary>
        internal static void LogError(string message)
        {
            _hadErrors = true;
            Log(message);
            try
            {
                if (_tempLogFile != null)
                    File.AppendAllText(_tempLogFile,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
