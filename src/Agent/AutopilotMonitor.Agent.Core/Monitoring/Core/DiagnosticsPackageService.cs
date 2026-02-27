using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Configuration;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Agent.Core.Monitoring.Network;
using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Agent.Core.Monitoring.Core
{
    /// <summary>
    /// Result of a diagnostics package upload attempt.
    /// </summary>
    public class DiagnosticsUploadResult
    {
        /// <summary>Blob name on success, null on failure or skip.</summary>
        public string BlobName { get; set; }

        /// <summary>
        /// First part of the SAS URL (up to and including the first query param) with the token truncated,
        /// so the event shows the target container without leaking the full signature.
        /// Example: "https://account.blob.core.windows.net/diagnostics?sp=(truncated)"
        /// </summary>
        public string SasUrlPrefix { get; set; }

        /// <summary>Human-readable error code/message when upload failed. Null on success or skip.</summary>
        public string ErrorCode { get; set; }

        public bool Success => BlobName != null;
    }

    /// <summary>
    /// Creates and uploads a diagnostics ZIP package (agent logs + IME logs + session info)
    /// to the tenant's Azure Blob Storage container via a short-lived SAS URL.
    /// The SAS URL is fetched on-demand just before upload and never stored in config or on disk.
    /// </summary>
    public class DiagnosticsPackageService
    {
        private readonly AgentConfiguration _configuration;
        private readonly AgentLogger _logger;
        private readonly BackendApiClient _apiClient;
        private readonly HttpClient _httpClient;

        private static readonly string ImeLogFolder =
            Environment.ExpandEnvironmentVariables(@"%ProgramData%\Microsoft\IntuneManagementExtension\Logs");

        private static readonly string AgentLogFolder =
            Environment.ExpandEnvironmentVariables(Constants.LogDirectory);

        public DiagnosticsPackageService(AgentConfiguration configuration, AgentLogger logger, BackendApiClient apiClient)
        {
            _configuration = configuration;
            _logger = logger;
            _apiClient = apiClient;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        }

        /// <summary>
        /// Creates a diagnostics ZIP and uploads it to the configured Blob Storage container.
        /// Returns a DiagnosticsUploadResult with BlobName set on success, or ErrorCode set on failure.
        /// Returns null if the upload was skipped (mode=Off, not configured, or OnFailure+succeeded).
        /// This method is non-fatal: all exceptions are caught and logged.
        /// </summary>
        /// <param name="enrollmentSucceeded">
        /// True for a successful enrollment (affects OnFailure mode check and sessioninfo.txt content).
        /// Pass true for WhiteGlove pre-provisioning (it succeeded up to this point).
        /// </param>
        /// <param name="fileNameSuffix">
        /// Optional suffix inserted before the .zip extension.
        /// Example: "preprov" → AgentDiagnostics-{sessionId}-{timestamp}-preprov.zip
        /// Null (default) → AgentDiagnostics-{sessionId}-{timestamp}.zip
        /// </param>
        public async Task<DiagnosticsUploadResult> CreateAndUploadAsync(bool enrollmentSucceeded, string fileNameSuffix = null)
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

                if (!_configuration.DiagnosticsUploadEnabled)
                {
                    _logger.Debug("Diagnostics upload skipped: not configured for this tenant");
                    return null;
                }

                if (string.Equals(mode, "OnFailure", StringComparison.OrdinalIgnoreCase) && enrollmentSucceeded)
                {
                    _logger.Info("Diagnostics upload skipped: enrollment succeeded and mode=OnFailure");
                    return null;
                }

                _logger.Info($"Creating diagnostics package (mode={mode}, enrollmentSucceeded={enrollmentSucceeded}{(fileNameSuffix != null ? $", suffix={fileNameSuffix}" : "")})...");

                // Build ZIP in memory
                var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                var suffix = string.IsNullOrEmpty(fileNameSuffix) ? "" : $"-{fileNameSuffix}";
                var zipFileName = $"AgentDiagnostics-{_configuration.SessionId}-{timestamp}{suffix}.zip";

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

                        // 4. Configured additional log paths (global + tenant, validated by guards)
                        foreach (var entry in _configuration.DiagnosticsLogPaths ?? new System.Collections.Generic.List<Shared.Models.DiagnosticsLogPath>())
                        {
                            if (!Collectors.DiagnosticsPathGuards.IsDiagnosticsPathAllowed(entry.Path))
                            {
                                _logger.Warning($"Diagnostics path blocked by guard: {entry.Path}");
                                continue;
                            }
                            var expandedPath = Environment.ExpandEnvironmentVariables(entry.Path);
                            var folder = Path.GetDirectoryName(expandedPath);
                            var pattern = Path.GetFileName(expandedPath);
                            if (string.IsNullOrEmpty(folder)) continue;
                            if (string.IsNullOrEmpty(pattern) || !pattern.Contains(".")) pattern = "*";
                            var zipFolder = $"AdditionalLogs/{Path.GetFileName(folder)}";
                            AddLogFiles(archive, folder, zipFolder, pattern);
                        }
                    }

                    zipBytes = ms.ToArray();
                }

                _logger.Info($"Diagnostics package created: {zipFileName} ({zipBytes.Length / 1024} KB)");

                // Fetch a short-lived upload URL just before uploading — never stored in config or on disk
                _logger.Info("Requesting diagnostics upload URL from backend...");
                var uploadUrlResponse = await _apiClient.GetDiagnosticsUploadUrlAsync(
                    _configuration.TenantId,
                    _configuration.SessionId,
                    zipFileName);

                if (uploadUrlResponse == null || !uploadUrlResponse.Success || string.IsNullOrEmpty(uploadUrlResponse.UploadUrl))
                {
                    var errorCode = uploadUrlResponse?.Message ?? "Failed to get diagnostics upload URL from backend";
                    _logger.Warning($"Failed to get diagnostics upload URL from backend — skipping upload: {errorCode}");
                    return new DiagnosticsUploadResult { ErrorCode = errorCode };
                }

                var sasUrlPrefix = BuildSasUrlPrefix(uploadUrlResponse.UploadUrl);

                // Upload to Blob Storage using the freshly obtained URL
                var (uploaded, uploadErrorCode) = await UploadToBlobStorageAsync(zipFileName, zipBytes, uploadUrlResponse.UploadUrl);
                if (uploaded)
                {
                    _logger.Info($"Diagnostics package uploaded successfully: {zipFileName}");
                    return new DiagnosticsUploadResult { BlobName = zipFileName, SasUrlPrefix = sasUrlPrefix };
                }

                return new DiagnosticsUploadResult { SasUrlPrefix = sasUrlPrefix, ErrorCode = uploadErrorCode };
            }
            catch (Exception ex)
            {
                _logger.Warning($"Diagnostics package creation/upload failed (non-fatal): {ex.Message}");
                return new DiagnosticsUploadResult { ErrorCode = ex.Message };
            }
        }

        /// <summary>
        /// Returns a safe, truncated SAS URL prefix for logging — shows account/container but not the token.
        /// Example: "https://account.blob.core.windows.net/diagnostics?sp=(truncated)"
        /// </summary>
        private static string BuildSasUrlPrefix(string sasUrl)
        {
            try
            {
                var qIndex = sasUrl.IndexOf('?');
                if (qIndex < 0) return sasUrl;

                // Keep only the first query param name to show which container/permissions are set
                var basePath = sasUrl.Substring(0, qIndex);
                var query = sasUrl.Substring(qIndex + 1);
                var firstParam = query.Split('&')[0].Split('=')[0];
                return $"{basePath}?{firstParam}=(truncated)";
            }
            catch
            {
                return "(url unavailable)";
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
        /// Uploads the ZIP bytes to Azure Blob Storage via PUT with the provided container SAS URL.
        /// Container SAS URL format: https://account.blob.core.windows.net/container?sv=...&sig=...
        /// Blob URL is constructed by inserting the blob name before the query string.
        /// Returns (success, errorCode) — errorCode is null on success, otherwise the last HTTP error.
        /// </summary>
        private async Task<(bool success, string errorCode)> UploadToBlobStorageAsync(string blobName, byte[] data, string containerSasUrl)
        {
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
            string lastErrorCode = null;
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
                        return (true, null);
                    }

                    lastErrorCode = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger.Warning($"Blob upload attempt {attempt} failed: {lastErrorCode} - {responseBody}");
                }
                catch (Exception ex)
                {
                    lastErrorCode = ex.Message;
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
            return (false, lastErrorCode);
        }
    }
}
