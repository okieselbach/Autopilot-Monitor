using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using AutopilotMonitor.Shared;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Agent
{
    /// <summary>
    /// Fast self-update at agent startup: checks version.json in blob storage,
    /// downloads the ZIP if newer, swaps files (rename-trick for locked binaries),
    /// and restarts via PowerShell Wait-Process.
    ///
    /// Design priority: speed over update — better to run the old version than delay startup.
    /// </summary>
    static class SelfUpdater
    {
        private const int VersionCheckTimeoutMs = 1000;  // 1s — aggressive, skip if slow
        private const int DownloadTimeoutMs = 10000;     // 10s — abort if too slow
        private const string OldFileSuffix = ".old";

        /// <summary>
        /// Writes an init banner to the log file to visually separate this agent process
        /// from install-mode logs that share the same file.
        /// </summary>
        public static void LogInit(string agentVersion)
        {
            LogToFile($"======================= Agent init (v{agentVersion}) =======================");
        }

        /// <summary>
        /// Writes a message to the agent log file. Used for pre-update logging
        /// when the full AgentLogger is not yet initialized.
        /// </summary>
        public static void Log(string message)
        {
            LogToFile(message);
        }

        /// <summary>
        /// Deletes leftover .old files from a previous self-update.
        /// Called early in startup before any other logic.
        /// </summary>
        public static void CleanupPreviousUpdate(string agentDir, Action<string> log)
        {
            try
            {
                if (!Directory.Exists(agentDir))
                    return;

                foreach (var oldFile in Directory.GetFiles(agentDir, "*" + OldFileSuffix))
                {
                    try
                    {
                        File.Delete(oldFile);
                    }
                    catch
                    {
                        // Best-effort: file may still be locked if previous process hasn't fully exited
                    }
                }

                // Also clean up any leftover staging directory
                var stagingDir = Environment.ExpandEnvironmentVariables(Constants.AgentUpdateStagingDir);
                if (Directory.Exists(stagingDir))
                {
                    try { Directory.Delete(stagingDir, true); } catch { }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"Self-update cleanup warning: {ex.Message}");
            }
        }

        /// <summary>
        /// Optional: backend-provided SHA-256 hash of the latest agent ZIP.
        /// Set from the last AgentConfigResponse before calling CheckAndApplyUpdateAsync.
        /// Takes priority over the hash in version.json (separate trust channel).
        /// </summary>
        public static string BackendExpectedSha256 { get; set; }

        /// <summary>
        /// Checks for a newer agent version and applies the update if available.
        /// On success, restarts the process and never returns.
        /// On any failure, returns normally so the current version continues.
        /// </summary>
        public static async Task CheckAndApplyUpdateAsync(string currentVersion, string agentDir, bool consoleMode)
        {
            Action<string> log = msg =>
            {
                LogToFile(msg);
                if (consoleMode) Console.WriteLine(msg);
            };

            try
            {
                // Step 1: Fetch version.json (1s timeout)
                var (latestVersion, manifestSha256) = await GetLatestVersionAsync(log);
                if (latestVersion == null)
                    return; // Could not determine latest version — continue with current

                // Step 2: Compare versions
                if (!IsNewerVersion(currentVersion, latestVersion))
                {
                    log($"Self-update: current version {currentVersion} is up to date (latest: {latestVersion})");
                    return;
                }

                log($"Self-update: newer version available — current={currentVersion}, latest={latestVersion}");

                // Step 3: Download ZIP (10s timeout)
                var zipPath = Path.Combine(Path.GetTempPath(), "AutopilotMonitor-Agent-Update.zip");
                if (!await DownloadZipAsync(zipPath, log))
                    return;

                // Step 3b: Verify SHA-256 integrity (backend hash has priority over version.json hash)
                string expectedSha256;
                if (!string.IsNullOrEmpty(BackendExpectedSha256))
                {
                    expectedSha256 = BackendExpectedSha256;
                    log("Self-update: using backend hash for integrity verification (cached config — trusted channel)");
                }
                else if (!string.IsNullOrEmpty(manifestSha256))
                {
                    expectedSha256 = manifestSha256;
                    log("Self-update: using version.json hash for integrity verification (blob storage)");
                }
                else
                {
                    expectedSha256 = null;
                }

                if (!VerifyZipIntegrity(zipPath, expectedSha256, log))
                {
                    CleanupStaging(null, zipPath);
                    return;
                }

                // Step 4: Extract to staging directory
                var stagingDir = Environment.ExpandEnvironmentVariables(Constants.AgentUpdateStagingDir);
                if (!ExtractToStaging(zipPath, stagingDir, log))
                    return;

                // Step 5: Validate staging
                var stagedExe = Path.Combine(stagingDir, "AutopilotMonitor.Agent.exe");
                if (!File.Exists(stagedExe))
                {
                    log("Self-update: staging validation failed — AutopilotMonitor.Agent.exe not found in ZIP");
                    CleanupStaging(stagingDir, zipPath);
                    return;
                }

                // Step 6: Swap files (rename locked → .old, copy new)
                if (!SwapFiles(agentDir, stagingDir, log))
                {
                    CleanupStaging(stagingDir, zipPath);
                    return;
                }

                // Step 7: Clean up staging + temp ZIP
                CleanupStaging(stagingDir, zipPath);

                // Step 8: Restart via PowerShell Wait-Process
                log($"Self-update: files swapped successfully, restarting agent (v{latestVersion})...");
                RestartAgent(agentDir, log);

                // RestartAgent calls Environment.Exit — we should never reach here
            }
            catch (Exception ex)
            {
                log($"Self-update: unexpected error — {ex.Message}. Continuing with current version.");
            }
        }

        /// <summary>
        /// Fetches version.json from blob storage and returns the version string and optional SHA-256 hash.
        /// Returns (null, null) if the check fails or times out.
        /// </summary>
        private static async Task<(string version, string sha256)> GetLatestVersionAsync(Action<string> log)
        {
            try
            {
                var versionUrl = $"{Constants.AgentBlobBaseUrl}/{Constants.AgentVersionFileName}";

                using (var handler = new HttpClientHandler())
                using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(VersionCheckTimeoutMs) })
                {
                    var json = await client.GetStringAsync(versionUrl);
                    var obj = JObject.Parse(json);
                    var version = obj["version"]?.ToString();

                    if (string.IsNullOrWhiteSpace(version))
                    {
                        log("Self-update: version.json has no 'version' field");
                        return (null, null);
                    }

                    var sha256 = obj["sha256"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(sha256))
                        log($"Self-update: version.json has SHA-256 hash for integrity verification");

                    return (version.Trim(), sha256?.Trim());
                }
            }
            catch (TaskCanceledException)
            {
                log("Self-update: version check timed out (1s) — skipping update");
                return (null, null);
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("404"))
            {
                log("Self-update: version.json not found (404) — skipping update");
                return (null, null);
            }
            catch (Exception ex)
            {
                log($"Self-update: version check failed — {ex.Message}");
                return (null, null);
            }
        }

        /// <summary>
        /// Verifies the SHA-256 hash of the downloaded ZIP against the expected hash.
        /// Returns true if the hash matches or if no expected hash is available (backward compat).
        /// </summary>
        private static bool VerifyZipIntegrity(string zipPath, string expectedSha256, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(expectedSha256))
            {
                log("Self-update: no SHA-256 hash available — skipping integrity check (backward compat)");
                return true;
            }

            try
            {
                using (var sha256 = SHA256.Create())
                using (var stream = File.OpenRead(zipPath))
                {
                    var hashBytes = sha256.ComputeHash(stream);
                    var actualHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

                    if (string.Equals(actualHash, expectedSha256.ToLowerInvariant(), StringComparison.Ordinal))
                    {
                        log("Self-update: SHA-256 integrity check passed");
                        return true;
                    }

                    log($"Self-update: SHA-256 MISMATCH — expected={expectedSha256}, actual={actualHash}. Aborting update.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                log($"Self-update: SHA-256 verification failed — {ex.Message}. Aborting update.");
                return false;
            }
        }

        /// <summary>
        /// Compares two version strings. Returns true if latest is newer than current.
        /// Strips SemVer suffixes (+metadata, -prerelease) before parsing because
        /// System.Version cannot handle them (e.g. "1.0.386+b7f8d3c..." would fail).
        /// </summary>
        private static bool IsNewerVersion(string current, string latest)
        {
            if (!Version.TryParse(StripVersionSuffix(current), out var currentVer))
                return false;
            if (!Version.TryParse(StripVersionSuffix(latest), out var latestVer))
                return false;

            return latestVer > currentVer;
        }

        private static string StripVersionSuffix(string version)
        {
            if (string.IsNullOrEmpty(version))
                return version;

            var plusIndex = version.IndexOf('+');
            if (plusIndex >= 0)
                version = version.Substring(0, plusIndex);

            var dashIndex = version.IndexOf('-');
            if (dashIndex >= 0)
                version = version.Substring(0, dashIndex);

            return version;
        }

        /// <summary>
        /// Downloads the agent ZIP to a temp path. Returns false on failure.
        /// </summary>
        private static async Task<bool> DownloadZipAsync(string zipPath, Action<string> log)
        {
            try
            {
                var zipUrl = $"{Constants.AgentBlobBaseUrl}/{Constants.AgentZipFileName}";

                // Clean up any previous download
                if (File.Exists(zipPath))
                    File.Delete(zipPath);

                using (var handler = new HttpClientHandler())
                using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(DownloadTimeoutMs) })
                {
                    using (var response = await client.GetAsync(zipUrl))
                    {
                        response.EnsureSuccessStatusCode();
                        using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            await response.Content.CopyToAsync(fileStream);
                        }
                    }
                }

                log($"Self-update: ZIP downloaded ({new FileInfo(zipPath).Length / 1024}KB)");
                return true;
            }
            catch (TaskCanceledException)
            {
                log("Self-update: ZIP download timed out (10s) — aborting update");
                try { File.Delete(zipPath); } catch { }
                return false;
            }
            catch (Exception ex)
            {
                log($"Self-update: ZIP download failed — {ex.Message}");
                try { File.Delete(zipPath); } catch { }
                return false;
            }
        }

        /// <summary>
        /// Extracts the ZIP to the staging directory. Returns false on failure.
        /// </summary>
        private static bool ExtractToStaging(string zipPath, string stagingDir, Action<string> log)
        {
            try
            {
                // Clean up any previous staging
                if (Directory.Exists(stagingDir))
                    Directory.Delete(stagingDir, true);

                ZipFile.ExtractToDirectory(zipPath, stagingDir);
                log($"Self-update: ZIP extracted to staging ({Directory.GetFiles(stagingDir).Length} files)");
                return true;
            }
            catch (Exception ex)
            {
                log($"Self-update: ZIP extraction failed — {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Swaps files from staging into the agent directory.
        /// For locked files: rename to .old (Windows allows renaming locked files), then copy new.
        /// Returns false if the swap fails critically.
        /// </summary>
        private static bool SwapFiles(string agentDir, string stagingDir, Action<string> log)
        {
            try
            {
                var stagedFiles = Directory.GetFiles(stagingDir);
                int swapped = 0;

                foreach (var stagedFile in stagedFiles)
                {
                    var fileName = Path.GetFileName(stagedFile);
                    var targetPath = Path.Combine(agentDir, fileName);

                    try
                    {
                        if (File.Exists(targetPath))
                        {
                            // Try direct overwrite first
                            try
                            {
                                File.Copy(stagedFile, targetPath, overwrite: true);
                                swapped++;
                                continue;
                            }
                            catch (IOException)
                            {
                                // File is locked — use rename trick
                            }

                            // Rename locked file to .old (Windows allows this even for locked files)
                            var oldPath = targetPath + OldFileSuffix;
                            if (File.Exists(oldPath))
                            {
                                try { File.Delete(oldPath); } catch { }
                            }

                            File.Move(targetPath, oldPath);
                        }

                        File.Copy(stagedFile, targetPath);
                        swapped++;
                    }
                    catch (Exception ex)
                    {
                        log($"Self-update: failed to swap {fileName} — {ex.Message}");
                        // Continue with other files; partial update is better than no update
                        // for non-critical files (configs, etc.)
                    }
                }

                log($"Self-update: swapped {swapped}/{stagedFiles.Length} files");

                // Critical check: the main exe must have been swapped
                var mainExe = Path.Combine(agentDir, "AutopilotMonitor.Agent.exe");
                if (!File.Exists(mainExe))
                {
                    log("Self-update: CRITICAL — main exe missing after swap, aborting");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                log($"Self-update: file swap failed — {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Restarts the agent using PowerShell Wait-Process (same pattern as SummaryDialog.SelfCleanup).
        /// Wait-Process uses OS handles — zero polling, exits within milliseconds of process termination.
        /// </summary>
        private static void RestartAgent(string agentDir, Action<string> log)
        {
            var pid = Process.GetCurrentProcess().Id;
            var agentExePath = Path.Combine(agentDir, "AutopilotMonitor.Agent.exe");

            // PowerShell Wait-Process: waits for our process to actually exit (no polling),
            // then immediately starts the new agent. 30s timeout as safety net.
            var psScript = $"Wait-Process -Id {pid} -Timeout 30 -ErrorAction SilentlyContinue; " +
                           $"& '{agentExePath}'";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(psi);

            log("Self-update: restart script launched, exiting current process");
            Environment.Exit(0);
        }

        /// <summary>
        /// Cleans up staging directory and temp ZIP file.
        /// </summary>
        private static void CleanupStaging(string stagingDir, string zipPath)
        {
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
        }

        /// <summary>
        /// Appends a log line to the main agent log file (logger isn't initialized yet at update time).
        /// Uses the same date-based naming and format as AgentLogger: agent_YYYYMMDD.log
        /// </summary>
        private static void LogToFile(string message)
        {
            try
            {
                var logDir = Environment.ExpandEnvironmentVariables(Constants.LogDirectory);
                Directory.CreateDirectory(logDir);
                var logFileName = $"agent_{DateTime.Now:yyyyMMdd}.log";
                var logPath = Path.Combine(logDir, logFileName);
                File.AppendAllText(logPath, $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] [INFO] {message}{Environment.NewLine}");
            }
            catch
            {
                // Best-effort logging — never block on log failure
            }
        }
    }
}
