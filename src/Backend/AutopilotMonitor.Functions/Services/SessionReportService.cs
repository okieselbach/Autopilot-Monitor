using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Manages session report submissions from Tenant Admins.
    /// Creates a ZIP with session data, events, analysis results, timeline TXT, events CSV,
    /// and optional screenshot, uploads to central blob storage, and stores metadata in Table Storage.
    /// </summary>
    public class SessionReportService
    {
        private readonly TableStorageService _storageService;
        private readonly ILogger<SessionReportService> _logger;
        private readonly string _blobConnectionString;
        private const string ContainerName = "session-reports";

        public SessionReportService(
            TableStorageService storageService,
            IConfiguration configuration,
            ILogger<SessionReportService> logger)
        {
            _storageService = storageService;
            _logger = logger;
            _blobConnectionString = configuration["AzureBlobStorageConnectionString"]
                ?? throw new InvalidOperationException("AzureBlobStorageConnectionString is not configured");
        }

        /// <summary>
        /// Creates a ZIP from the provided data, uploads to central blob storage,
        /// and records metadata in the SessionReports table.
        /// </summary>
        public async Task<SessionReportMetadata> SubmitReportAsync(
            SubmitSessionReportRequest request,
            string submittedBy)
        {
            var reportId = Guid.NewGuid().ToString("N")[..12];
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var blobName = $"{request.TenantId}_{request.SessionId}_diag_request_{timestamp}.zip";

            // 1. Create ZIP in memory
            using var zipStream = new MemoryStream();
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                AddJsonEntry(archive, "session.json", request.SessionData);
                AddJsonEntry(archive, "events.json", request.EventsData);
                AddJsonEntry(archive, "analysis-results.json", request.AnalysisResultsData);

                // Timeline TXT export (UI representation)
                if (!string.IsNullOrEmpty(request.TimelineExportTxt))
                {
                    AddTextEntry(archive, "timeline.txt", request.TimelineExportTxt);
                }

                // Events CSV export (raw table data)
                if (!string.IsNullOrEmpty(request.EventsCsv))
                {
                    AddTextEntry(archive, "events.csv", request.EventsCsv);
                }

                // Report metadata
                AddJsonEntry(archive, "report-metadata.json", new
                {
                    reportId,
                    request.TenantId,
                    request.SessionId,
                    request.Comment,
                    request.Email,
                    submittedBy,
                    submittedAt = DateTime.UtcNow.ToString("O")
                });

                // Optional screenshot
                if (!string.IsNullOrEmpty(request.ScreenshotBase64))
                {
                    try
                    {
                        var screenshotBytes = Convert.FromBase64String(request.ScreenshotBase64);
                        var ext = Path.GetExtension(request.ScreenshotFileName ?? ".png");
                        if (string.IsNullOrEmpty(ext)) ext = ".png";
                        var entry = archive.CreateEntry($"screenshot{ext}", CompressionLevel.Optimal);
                        using var entryStream = entry.Open();
                        await entryStream.WriteAsync(screenshotBytes);
                    }
                    catch (FormatException ex)
                    {
                        _logger.LogWarning(ex, "Invalid base64 screenshot data in report {ReportId}", reportId);
                    }
                }

                // Optional agent log file (max 5 MB enforced by frontend)
                if (!string.IsNullOrEmpty(request.AgentLogBase64))
                {
                    try
                    {
                        var logBytes = Convert.FromBase64String(request.AgentLogBase64);
                        var logFileName = request.AgentLogFileName ?? "agent.log";
                        var entry = archive.CreateEntry(logFileName, CompressionLevel.Optimal);
                        using var entryStream = entry.Open();
                        await entryStream.WriteAsync(logBytes);
                    }
                    catch (FormatException ex)
                    {
                        _logger.LogWarning(ex, "Invalid base64 agent log data in report {ReportId}", reportId);
                    }
                }
            }

            // 2. Upload ZIP to central blob storage
            zipStream.Position = 0;
            var blobServiceClient = new BlobServiceClient(_blobConnectionString);
            var containerClient = blobServiceClient.GetBlobContainerClient(ContainerName);
            await containerClient.CreateIfNotExistsAsync();
            var blobClient = containerClient.GetBlobClient(blobName);
            await blobClient.UploadAsync(zipStream, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/zip" }
            });

            _logger.LogInformation(
                "Session report uploaded: ReportId={ReportId}, BlobName={BlobName}, Tenant={TenantId}, Session={SessionId}",
                reportId, blobName, request.TenantId, request.SessionId);

            // 3. Store metadata in Table Storage
            var now = DateTime.UtcNow;
            var invertedTicks = (DateTime.MaxValue.Ticks - now.Ticks).ToString("D19");
            var entity = new TableEntity("reports", $"{invertedTicks}_{reportId}")
            {
                ["ReportId"] = reportId,
                ["TenantId"] = request.TenantId ?? string.Empty,
                ["SessionId"] = request.SessionId ?? string.Empty,
                ["Comment"] = request.Comment ?? string.Empty,
                ["Email"] = request.Email ?? string.Empty,
                ["BlobName"] = blobName,
                ["SubmittedBy"] = submittedBy ?? string.Empty,
                ["SubmittedAt"] = now
            };

            var tableClient = _storageService.GetTableServiceClient().GetTableClient(Constants.TableNames.SessionReports);
            await tableClient.UpsertEntityAsync(entity);

            return new SessionReportMetadata
            {
                ReportId = reportId,
                TenantId = request.TenantId,
                SessionId = request.SessionId,
                Comment = request.Comment,
                Email = request.Email,
                BlobName = blobName,
                SubmittedBy = submittedBy,
                SubmittedAt = now
            };
        }

        /// <summary>
        /// Returns all session reports, newest first (by inverted RowKey).
        /// </summary>
        public async Task<List<SessionReportMetadata>> GetAllReportsAsync()
        {
            var tableClient = _storageService.GetTableServiceClient().GetTableClient(Constants.TableNames.SessionReports);
            var results = new List<SessionReportMetadata>();

            await foreach (var entity in tableClient.QueryAsync<TableEntity>(e => e.PartitionKey == "reports"))
            {
                results.Add(new SessionReportMetadata
                {
                    ReportId = entity.GetString("ReportId") ?? string.Empty,
                    TenantId = entity.GetString("TenantId") ?? string.Empty,
                    SessionId = entity.GetString("SessionId") ?? string.Empty,
                    Comment = entity.GetString("Comment") ?? string.Empty,
                    Email = entity.GetString("Email") ?? string.Empty,
                    BlobName = entity.GetString("BlobName") ?? string.Empty,
                    SubmittedBy = entity.GetString("SubmittedBy") ?? string.Empty,
                    SubmittedAt = entity.GetDateTimeOffset("SubmittedAt")?.UtcDateTime ?? DateTime.MinValue
                });
            }

            return results;
        }

        private static void AddJsonEntry(ZipArchive archive, string name, object data)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(JsonConvert.SerializeObject(data, Formatting.Indented));
        }

        private static void AddTextEntry(ZipArchive archive, string name, string content)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }
    }
}
