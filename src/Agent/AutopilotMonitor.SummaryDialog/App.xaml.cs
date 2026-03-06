using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace AutopilotMonitor.SummaryDialog
{
    public partial class App : Application
    {
        internal static string StatusFilePath { get; private set; }
        internal static int TimeoutSeconds { get; private set; } = 60;
        internal static string BrandingImageUrl { get; private set; }
        internal static bool? ForceTheme { get; private set; } // true=dark, false=light, null=auto

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ParseArguments(e.Args);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            base.OnExit(e);
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
        /// Self-cleanup: delete the temp folder this EXE was launched from.
        /// Uses a cmd.exe script that waits for our process to exit, then deletes the folder.
        /// </summary>
        private void SelfCleanup()
        {
            try
            {
                var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                var exeDir = Path.GetDirectoryName(exePath);

                // Only self-cleanup if running from a known staging folder
                // (agent copies the dialog to ProgramData\AutopilotMonitor\Summary-* or legacy TEMP)
                if (exeDir == null)
                    return;

                var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                var agentStagingRoot = Path.Combine(programData, "AutopilotMonitor");
                var tempRoot = Path.GetTempPath().TrimEnd('\\');

                var isStagedCopy = exeDir.StartsWith(agentStagingRoot, StringComparison.OrdinalIgnoreCase)
                                || exeDir.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase);
                if (!isStagedCopy)
                    return;

                var pid = Process.GetCurrentProcess().Id;
                // Wait for our process to exit, then delete the temp folder
                var script = $@"/c tasklist /fi ""PID eq {pid}"" 2>nul | find ""{pid}"" >nul && (ping 127.0.0.1 -n 3 >nul) & rd /s /q ""{exeDir}""";

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = script,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(psi);
            }
            catch
            {
                // Best-effort cleanup, ignore errors
            }
        }
    }
}
