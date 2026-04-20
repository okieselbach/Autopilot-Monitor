using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Shared;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2
{
    /// <summary>
    /// <c>--install</c> mode: deploys the agent payload to the canonical install directory,
    /// persists bootstrap and await-enrollment config for the Scheduled Task, registers + starts
    /// the task, and writes the deployment registry marker. Plan §4.x M4.6.α.
    /// <para>
    /// Ported 1:1 from Legacy <c>Program.InstallMode.cs</c> — the OOBE bootstrap script and the
    /// Intune Platform Script contract expect exactly this sequence and these file locations.
    /// The only delta is the V2 exe name / build-output directory.
    /// </para>
    /// </summary>
    public static partial class Program
    {
        internal const string DeploymentRegistryKey = @"SOFTWARE\AutopilotMonitor";
        internal const string DeploymentRegistryValue = "Deployed";
        internal const string BootstrapConfigFileName = "bootstrap-config.json";
        internal const string AwaitEnrollmentConfigFileName = "await-enrollment.json";
        private const string InstalledAgentExeName = "AutopilotMonitor.Agent.V2.exe";

        internal static int RunInstallMode(string[] args)
        {
            var logDir = Environment.ExpandEnvironmentVariables(Constants.LogDirectory);
            var logger = new AgentLogger(logDir, AgentLogLevel.Debug);
            var consoleMode = args.Contains("--console") || Environment.UserInteractive;
            logger.EnableConsoleOutput = consoleMode;

            try
            {
                logger.Info("======================= Agent install mode (--install) =======================");

                EnsureAgentDirectories(logger);

                var sourceExePath = Assembly.GetExecutingAssembly().Location;
                var sourceDir = Path.GetDirectoryName(sourceExePath) ?? string.Empty;
                var targetAgentDir = Environment.ExpandEnvironmentVariables(
                    Path.Combine(Constants.AgentDataDirectory, DefaultAgentSubdirectory));
                var targetExePath = Path.Combine(targetAgentDir, InstalledAgentExeName);

                if (!string.Equals(
                        Path.GetFullPath(sourceDir).TrimEnd('\\'),
                        Path.GetFullPath(targetAgentDir).TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase))
                {
                    logger.Info($"Install mode called from '{sourceDir}'. Deploying payload to '{targetAgentDir}'.");
                    CopyDirectory(sourceDir, targetAgentDir, logger);
                }
                else
                {
                    logger.Info("Agent already running from target install directory; payload copy not required.");
                }

                // Persist bootstrap config if --bootstrap-token was provided. The Scheduled Task
                // command line has no args — the agent picks this up on the first post-install run.
                var bootstrapTokenArg = GetArgValue(args, "--bootstrap-token");
                var tenantIdArg = GetArgValue(args, "--tenant-id");
                if (!string.IsNullOrEmpty(bootstrapTokenArg))
                {
                    var bootstrapConfigPath = Path.Combine(
                        Environment.ExpandEnvironmentVariables(Constants.AgentDataDirectory),
                        BootstrapConfigFileName);
                    var bootstrapConfig = new BootstrapConfigFile
                    {
                        BootstrapToken = bootstrapTokenArg,
                        TenantId = tenantIdArg,
                    };
                    File.WriteAllText(bootstrapConfigPath, JsonConvert.SerializeObject(bootstrapConfig));
                    logger.Info("Bootstrap config persisted for Scheduled Task.");
                }

                // Persist await-enrollment config if requested.
                if (args.Contains("--await-enrollment"))
                {
                    var awaitConfigPath = Path.Combine(
                        Environment.ExpandEnvironmentVariables(Constants.AgentDataDirectory),
                        AwaitEnrollmentConfigFileName);
                    var awaitTimeoutArg = GetArgValue(args, "--await-enrollment-timeout");
                    var timeoutMinutes = 480;
                    if (!string.IsNullOrEmpty(awaitTimeoutArg) && int.TryParse(awaitTimeoutArg, out var parsed))
                        timeoutMinutes = parsed;

                    var awaitConfig = new AwaitEnrollmentConfigFile { TimeoutMinutes = timeoutMinutes };
                    File.WriteAllText(awaitConfigPath, JsonConvert.SerializeObject(awaitConfig));
                    logger.Info($"Await-enrollment config persisted for Scheduled Task (timeout: {timeoutMinutes}min).");
                }

                var taskName = Constants.ScheduledTaskName;
                var taskCommand = $"\"{targetExePath}\"";

                logger.Info($"Registering Scheduled Task '{taskName}' for executable: {targetExePath}");

                var createExitCode = RunProcess(
                    "schtasks.exe",
                    $"/Create /TN \"{taskName}\" /TR \"{taskCommand}\" /SC ONSTART /RU SYSTEM /RL HIGHEST /F",
                    logger);

                if (createExitCode != 0)
                    throw new InvalidOperationException(
                        $"Failed to create/update Scheduled Task '{taskName}' (exit code {createExitCode}).");

                logger.Info($"Scheduled Task '{taskName}' created/updated successfully.");

                var startExitCode = RunProcess(
                    "schtasks.exe",
                    $"/Run /TN \"{taskName}\"",
                    logger);

                if (startExitCode != 0)
                    throw new InvalidOperationException(
                        $"Failed to start Scheduled Task '{taskName}' (exit code {startExitCode}).");

                logger.Info($"Scheduled Task '{taskName}' started successfully.");

                TryWriteDeploymentMarker(logger);

                if (consoleMode)
                {
                    Console.WriteLine("Installation completed successfully.");
                    Console.WriteLine($"Task: {taskName}");
                    Console.WriteLine($"Executable: {targetExePath}");
                    Console.WriteLine($"Log: {logDir}");
                }

                return 0;
            }
            catch (Exception ex)
            {
                logger.Error("Install mode failed.", ex);
                if (consoleMode) Console.Error.WriteLine($"ERROR: {ex.Message}");
                return 1;
            }
        }

        private static void EnsureAgentDirectories(AgentLogger logger)
        {
            var basePath = Environment.ExpandEnvironmentVariables(Constants.AgentDataDirectory);
            var paths = new[]
            {
                basePath,
                Path.Combine(basePath, DefaultAgentSubdirectory),
                Environment.ExpandEnvironmentVariables(Constants.SpoolDirectory),
                Environment.ExpandEnvironmentVariables(Constants.LogDirectory),
            };

            foreach (var path in paths)
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                    logger.Info($"Created directory: {path}");
                }
                else
                {
                    logger.Debug($"Directory already exists: {path}");
                }
            }
        }

        private static int RunProcess(string fileName, string arguments, AgentLogger logger)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            logger.Info($"Executing: {fileName} {arguments}");

            using (var process = Process.Start(psi))
            {
                if (process == null)
                    throw new InvalidOperationException($"Failed to start process: {fileName}");

                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (!string.IsNullOrWhiteSpace(stdout)) logger.Info($"Process output: {stdout.Trim()}");
                if (!string.IsNullOrWhiteSpace(stderr)) logger.Warning($"Process error output: {stderr.Trim()}");

                logger.Info($"Process exit code: {process.ExitCode}");
                return process.ExitCode;
            }
        }

        private static void CopyDirectory(string sourceDir, string targetDir, AgentLogger logger)
        {
            if (!Directory.Exists(sourceDir))
                throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");

            Directory.CreateDirectory(targetDir);

            foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                var rel = dir.Substring(sourceDir.Length).TrimStart('\\');
                Directory.CreateDirectory(Path.Combine(targetDir, rel));
            }

            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var rel = file.Substring(sourceDir.Length).TrimStart('\\');
                var dest = Path.Combine(targetDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? targetDir);

                try
                {
                    File.Copy(file, dest, overwrite: true);
                }
                catch (Exception ex)
                {
                    // Keep going on locked destinations — the Scheduled Task will pick up the
                    // latest available payload, and the runtime self-update path will retry later.
                    if (File.Exists(dest))
                    {
                        logger.Warning($"Could not overwrite '{dest}': {ex.Message}. Keeping existing file.");
                        continue;
                    }
                    throw;
                }
            }

            logger.Info($"Payload deployment completed: '{sourceDir}' -> '{targetDir}'");
        }

        private static void TryWriteDeploymentMarker(AgentLogger logger)
        {
            try
            {
                using (var regKey = Registry.LocalMachine.CreateSubKey(DeploymentRegistryKey))
                {
                    regKey.SetValue(DeploymentRegistryValue, DateTime.UtcNow.ToString("O"));
                }
                logger.Info($"Deployment registry marker written (HKLM\\{DeploymentRegistryKey}\\{DeploymentRegistryValue}).");
            }
            catch (Exception ex)
            {
                logger.Warning($"Failed to write deployment registry marker: {ex.Message}");
            }
        }
    }
}
