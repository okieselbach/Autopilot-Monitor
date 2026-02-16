using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Configuration;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Agent.Core.Monitoring.Core
{
    /// <summary>
    /// Creates and uploads a diagnostics ZIP package (agent logs + IME logs + session info)
    /// directly to the tenant's Azure Blob Storage container via SAS URL.
    /// </summary>
    public class DiagnosticsPackageService
    {
        private readonly AgentConfiguration _configuration;
        private readonly AgentLogger _logger;
        private readonly HttpClient _httpClient;

        private static readonly string ImeLogFolder =
            Environment.ExpandEnvironmentVariables(@"%ProgramData%\Microsoft\IntuneManagementExtension\Logs");

        private static readonly string AgentLogFolder =
            Environment.ExpandEnvironmentVariables(Constants.LogDirectory);

        public DiagnosticsPackageService(AgentConfiguration configuration, AgentLogger logger)
        {
            _configuration = configuration;
            _logger = logger;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        }

        /// <summary>
        /// Creates a diagnostics ZIP and uploads it to the configured Blob Storage container.
        /// Returns the blob name on success, or null if skipped or failed.
        /// This method is non-fatal: all exceptions are caught and logged.
        /// </summary>
        public async Task<string> CreateAndUploadAsync(bool enrollmentSucceeded)
        {
            try
            {
                // Check if upload is needed based on configuration
                var mode = _configuration.DiagnosticsUploadMode ?? "Off";
                if (string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Debug("Diagnostics upload disabled (mode=Off)");
                    return null;
                }

                if (string.IsNullOrEmpty(_configuration.DiagnosticsBlobSasUrl))
                {
                    _logger.Debug("Diagnostics upload skipped: no Blob Storage SAS URL configured");
                    return null;
                }

                if (string.Equals(mode, "OnFailure", StringComparison.OrdinalIgnoreCase) && enrollmentSucceeded)
                {
                    _logger.Info("Diagnostics upload skipped: enrollment succeeded and mode=OnFailure");
                    return null;
                }

                _logger.Info($"Creating diagnostics package (mode={mode}, enrollmentSucceeded={enrollmentSucceeded})...");

                // Build ZIP in memory
                var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                var zipFileName = $"AgentDiagnostics-{_configuration.SessionId}-{timestamp}.zip";

                byte[] zipBytes;
                using (var ms = new MemoryStream())
                {
                    using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        // 1. sessioninfo.txt
                        AddSessionInfo(archive, enrollmentSucceeded);

                        // 2. Agent logs
                        AddLogFiles(archive, AgentLogFolder, "AgentLogs", "*.log");

                        // 3. IME logs
                        AddLogFiles(archive, ImeLogFolder, "ImeLogs", "IntuneManagementExtension*.log");
                        AddLogFiles(archive, ImeLogFolder, "ImeLogs", "AppWorkload*.log");
                    }

                    zipBytes = ms.ToArray();
                }

                _logger.Info($"Diagnostics package created: {zipFileName} ({zipBytes.Length / 1024} KB)");

                // Upload to Blob Storage
                var uploaded = await UploadToBlobStorageAsync(zipFileName, zipBytes);
                if (uploaded)
                {
                    _logger.Info($"Diagnostics package uploaded successfully: {zipFileName}");
                    return zipFileName;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.Warning($"Diagnostics package creation/upload failed (non-fatal): {ex.Message}");
                return null;
            }
        }

        private void AddSessionInfo(ZipArchive archive, bool enrollmentSucceeded)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Session ID: {_configuration.SessionId}");
            sb.AppendLine($"Tenant ID: {_configuration.TenantId}");
            sb.AppendLine($"Device Name: {Environment.MachineName}");
            sb.AppendLine($"Timestamp: {DateTime.UtcNow:O}");
            sb.AppendLine($"Enrollment Result: {(enrollmentSucceeded ? "Succeeded" : "Failed")}");

            // Hardware info via existing DeviceInfoProvider (WMI)
            sb.AppendLine($"Manufacturer: {DeviceInfoProvider.GetManufacturer()}");
            sb.AppendLine($"Model: {DeviceInfoProvider.GetModel()}");
            sb.AppendLine($"Serial Number: {DeviceInfoProvider.GetSerialNumber()}");

            var entry = archive.CreateEntry("sessioninfo.txt");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(sb.ToString());
        }

        private void AddLogFiles(ZipArchive archive, string sourceFolder, string zipFolder, string searchPattern)
        {
            if (!Directory.Exists(sourceFolder))
            {
                _logger.Debug($"Log folder not found, skipping: {sourceFolder}");
                return;
            }

            try
            {
                var files = Directory.GetFiles(sourceFolder, searchPattern);
                foreach (var file in files)
                {
                    try
                    {
                        var fileName = Path.GetFileName(file);
                        var entryName = $"{zipFolder}/{fileName}";

                        // Read with FileShare.ReadWrite to avoid locking conflicts with active log writers
                        byte[] content;
                        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var reader = new MemoryStream())
                        {
                            fs.CopyTo(reader);
                            content = reader.ToArray();
                        }

                        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                        using var entryStream = entry.Open();
                        entryStream.Write(content, 0, content.Length);

                        _logger.Debug($"Added to diagnostics package: {entryName} ({content.Length / 1024} KB)");
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"Failed to add log file to diagnostics package: {file} - {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to enumerate log files in {sourceFolder}: {ex.Message}");
            }
        }

        /// <summary>
        /// Uploads the ZIP bytes to the configured Azure Blob Storage container via PUT with SAS token.
        /// Container SAS URL format: https://account.blob.core.windows.net/container?sv=...&sig=...
        /// Blob URL is constructed by inserting the blob name before the query string.
        /// </summary>
        private async Task<bool> UploadToBlobStorageAsync(string blobName, byte[] data)
        {
            var containerSasUrl = _configuration.DiagnosticsBlobSasUrl;

            // Build blob URL: split at '?' to insert blob name
            var questionMarkIndex = containerSasUrl.IndexOf('?');
            string blobUrl;
            if (questionMarkIndex >= 0)
            {
                var basePath = containerSasUrl.Substring(0, questionMarkIndex).TrimEnd('/');
                var queryString = containerSasUrl.Substring(questionMarkIndex);
                blobUrl = $"{basePath}/{blobName}{queryString}";
            }
            else
            {
                // No query string (unlikely for SAS but handle gracefully)
                blobUrl = $"{containerSasUrl.TrimEnd('/')}/{blobName}";
            }

            const int maxRetries = 3;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    _logger.Info($"Uploading diagnostics package (attempt {attempt}/{maxRetries}, {data.Length / 1024} KB)...");

                    using var content = new ByteArrayContent(data);
                    content.Headers.Add("x-ms-blob-type", "BlockBlob");
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");

                    var response = await _httpClient.PutAsync(blobUrl, content);

                    if (response.IsSuccessStatusCode)
                    {
                        return true;
                    }

                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger.Warning($"Blob upload attempt {attempt} failed: HTTP {(int)response.StatusCode} - {responseBody}");
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Blob upload attempt {attempt} failed: {ex.Message}");
                }

                if (attempt < maxRetries)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 2s, 4s, 8s
                    _logger.Info($"Retrying blob upload in {delay.TotalSeconds}s...");
                    await Task.Delay(delay);
                }
            }

            _logger.Warning($"Diagnostics package upload failed after {maxRetries} attempts");
            return false;
        }
    }
}
