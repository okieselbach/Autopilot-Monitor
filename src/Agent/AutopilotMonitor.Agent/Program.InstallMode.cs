using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using AutopilotMonitor.Agent.Core.Configuration;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent
{
    partial class Program
    {
        static void RunInstallMode(string[] args)
        {
            var logDir = Environment.ExpandEnvironmentVariables(Constants.LogDirectory);
            var logger = new AgentLogger(logDir, AgentLogLevel.Debug);
            var consoleMode = args.Contains("--console") || Environment.UserInteractive;

            try
            {
                logger.Info("======================= Agent install mode (--install) =======================");

                EnsureAgentDirectories(logger);

                var sourceExePath = Assembly.GetExecutingAssembly().Location;
                var sourceDir = Path.GetDirectoryName(sourceExePath) ?? string.Empty;
                var targetAgentDir = Environment.ExpandEnvironmentVariables(Path.Combine(Constants.AgentDataDirectory, "Agent"));
                var targetExePath = Path.Combine(targetAgentDir, "AutopilotMonitor.Agent.exe");

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

                // Persist bootstrap config if --bootstrap-token was provided.
                // The Scheduled Task command line has no args, so the agent reads this file on restart.
                var bootstrapTokenArg = GetArgValue(args, "--bootstrap-token");
                var tenantIdArg = GetArgValue(args, "--tenant-id");
                if (!string.IsNullOrEmpty(bootstrapTokenArg))
                {
                    var dataDir = Environment.ExpandEnvironmentVariables(Constants.AgentDataDirectory);
                    var bootstrapConfigPath = Path.Combine(dataDir, "bootstrap-config.json");
                    var bootstrapConfig = new BootstrapConfigFile
                    {
                        BootstrapToken = bootstrapTokenArg,
                        TenantId = tenantIdArg
                    };
                    File.WriteAllText(bootstrapConfigPath, Newtonsoft.Json.JsonConvert.SerializeObject(bootstrapConfig));
                    logger.Info("Bootstrap config persisted for Scheduled Task");
                }

                // Persist await-enrollment config if --await-enrollment was provided.
                // The Scheduled Task command line has no args, so the agent reads this file on restart.
                if (args.Contains("--await-enrollment"))
                {
                    var dataDir = Environment.ExpandEnvironmentVariables(Constants.AgentDataDirectory);
                    var awaitConfigPath = Path.Combine(dataDir, "await-enrollment.json");
                    var awaitTimeoutArg = GetArgValue(args, "--await-enrollment-timeout");
                    var timeoutMinutes = 480;
                    if (!string.IsNullOrEmpty(awaitTimeoutArg) && int.TryParse(awaitTimeoutArg, out var parsed))
                        timeoutMinutes = parsed;

                    var awaitConfig = new AwaitEnrollmentConfigFile { TimeoutMinutes = timeoutMinutes };
                    File.WriteAllText(awaitConfigPath, Newtonsoft.Json.JsonConvert.SerializeObject(awaitConfig));
                    logger.Info($"Await-enrollment config persisted for Scheduled Task (timeout: {timeoutMinutes}min)");
                }

                var taskName = Constants.ScheduledTaskName;
                var taskCommand = $"\"{targetExePath}\"";

                logger.Info($"Registering Scheduled Task '{taskName}' for executable: {targetExePath}");

                var createExitCode = RunProcess(
                    "schtasks.exe",
                    $"/Create /TN \"{taskName}\" /TR \"{taskCommand}\" /SC ONSTART /RU SYSTEM /RL HIGHEST /F",
                    logger);

                if (createExitCode != 0)
                {
                    throw new InvalidOperationException($"Failed to create/update Scheduled Task '{taskName}' (exit code {createExitCode})");
                }

                logger.Info($"Scheduled Task '{taskName}' created/updated successfully");

                var startExitCode = RunProcess(
                    "schtasks.exe",
                    $"/Run /TN \"{taskName}\"",
                    logger);

                if (startExitCode != 0)
                {
                    throw new InvalidOperationException($"Failed to start Scheduled Task '{taskName}' (exit code {startExitCode})");
                }

                logger.Info($"Scheduled Task '{taskName}' started successfully");

                // Write deployment marker — survives self-destruct, prevents ghost re-installs.
                // Written here (not in bootstrap script) so manual --install also sets it.
                try
                {
                    using (var regKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\AutopilotMonitor"))
                    {
                        regKey.SetValue("Deployed", DateTime.UtcNow.ToString("O"));
                    }
                    logger.Info("Deployment registry marker written (HKLM\\SOFTWARE\\AutopilotMonitor\\Deployed)");
                }
                catch (Exception regEx)
                {
                    logger.Warning($"Failed to write deployment registry marker: {regEx.Message}");
                }

                if (consoleMode)
                {
                    Console.WriteLine("Installation completed successfully.");
                    Console.WriteLine($"Task: {taskName}");
                    Console.WriteLine($"Executable: {targetExePath}");
                    Console.WriteLine($"Log: {logDir}");
                }

                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                logger.Error("Registration mode failed", ex);
                if (consoleMode)
                {
                    Console.WriteLine($"ERROR: {ex.Message}");
                }
                Environment.Exit(1);
            }
        }

        private static void EnsureAgentDirectories(AgentLogger logger)
        {
            var basePath = Environment.ExpandEnvironmentVariables(Constants.AgentDataDirectory);
            var paths = new[]
            {
                basePath,
                Path.Combine(basePath, "Agent"),
                Environment.ExpandEnvironmentVariables(Constants.SpoolDirectory),
                Environment.ExpandEnvironmentVariables(Constants.LogDirectory)
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
                CreateNoWindow = true
            };

            logger.Info($"Executing: {fileName} {arguments}");

            using (var process = Process.Start(psi))
            {
                if (process == null)
                    throw new InvalidOperationException($"Failed to start process: {fileName}");

                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (!string.IsNullOrWhiteSpace(stdout))
                    logger.Info($"Process output: {stdout.Trim()}");
                if (!string.IsNullOrWhiteSpace(stderr))
                    logger.Warning($"Process error output: {stderr.Trim()}");

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
                    File.Copy(file, dest, true);
                }
                catch (Exception ex)
                {
                    // If destination exists, keep going and let scheduled task use the latest available payload.
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
    }
}
