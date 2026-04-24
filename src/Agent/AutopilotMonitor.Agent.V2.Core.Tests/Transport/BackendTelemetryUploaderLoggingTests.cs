using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Transport
{
    /// <summary>
    /// Plan §5 Fix 5 — <see cref="BackendTelemetryUploader"/> emits upload-cadence log lines
    /// (flushing / ingest OK / ingest TRANSIENT / ingest PERMANENT) so the agent log is no
    /// longer silent during the transport path. We assert on log-file content because the
    /// AgentLogger writes to disk; checking the file is the most faithful production proxy.
    /// </summary>
    public sealed class BackendTelemetryUploaderLoggingTests
    {
        private static readonly DateTime At = new DateTime(2026, 4, 24, 10, 0, 0, DateTimeKind.Utc);

        private static TelemetryItem NewEventItem(long id) =>
            new TelemetryItem(
                kind: TelemetryItemKind.Event,
                partitionKey: "T1_S1",
                rowKey: id.ToString("D10"),
                telemetryItemId: id,
                sessionTraceOrdinal: id,
                payloadJson: $"{{\"EventType\":\"test\",\"Sequence\":{id}}}",
                requiresImmediateFlush: false,
                enqueuedAtUtc: At);

        private static string ReadLogs(string path)
        {
            // AgentLogger writes to a dated file; scan the whole dir to stay robust to format.
            var sb = new System.Text.StringBuilder();
            foreach (var f in Directory.EnumerateFiles(path, "*.log", SearchOption.AllDirectories))
            {
                try
                {
                    // Opened with ReadWrite share so we don't collide with an in-flight logger.
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs);
                    sb.AppendLine(reader.ReadToEnd());
                }
                catch { /* best-effort */ }
            }
            return sb.ToString();
        }

        [Fact]
        public async Task UploadBatch_logs_flush_start_and_ingest_OK_on_success()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);

            var handler = new RecordingHttpMessageHandler();
            handler.QueueStatus(HttpStatusCode.OK);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            var sut = new BackendTelemetryUploader(
                httpClient: http,
                baseUrl: "https://backend.test",
                tenantId: "T1",
                logger: logger);

            var result = await sut.UploadBatchAsync(new[] { NewEventItem(1), NewEventItem(2) }, CancellationToken.None);
            Assert.True(result.Success);

            // AgentLogger writes each line with File.AppendAllText — content is already on disk.
            var logContent = ReadLogs(tmp.Path);
            Assert.Contains("BackendTelemetryUploader: flushing 2 item(s)", logContent);
            Assert.Contains("BackendTelemetryUploader: ingest OK (items=2", logContent);
        }

        [Fact]
        public async Task UploadBatch_logs_TRANSIENT_on_5xx()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);

            var handler = new RecordingHttpMessageHandler();
            handler.QueueStatus(HttpStatusCode.InternalServerError);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            var sut = new BackendTelemetryUploader(
                httpClient: http,
                baseUrl: "https://backend.test",
                tenantId: "T1",
                logger: logger);

            var result = await sut.UploadBatchAsync(new[] { NewEventItem(1) }, CancellationToken.None);
            Assert.False(result.Success);
            Assert.True(result.IsTransient);

            var logContent = ReadLogs(tmp.Path);
            Assert.Contains("BackendTelemetryUploader: ingest TRANSIENT", logContent);
        }

        [Fact]
        public async Task UploadBatch_logs_PERMANENT_on_4xx()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);

            var handler = new RecordingHttpMessageHandler();
            handler.QueueStatus(HttpStatusCode.BadRequest);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            var sut = new BackendTelemetryUploader(
                httpClient: http,
                baseUrl: "https://backend.test",
                tenantId: "T1",
                logger: logger);

            var result = await sut.UploadBatchAsync(new[] { NewEventItem(1) }, CancellationToken.None);
            Assert.False(result.Success);
            Assert.False(result.IsTransient);

            var logContent = ReadLogs(tmp.Path);
            Assert.Contains("BackendTelemetryUploader: ingest PERMANENT", logContent);
        }

        [Fact]
        public async Task UploadBatch_empty_batch_no_log_no_network()
        {
            // Short-circuit empty batch must not burn a log line or an HTTP call.
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);

            var handler = new RecordingHttpMessageHandler();
            using var http = new HttpClient(handler);

            var sut = new BackendTelemetryUploader(
                httpClient: http,
                baseUrl: "https://backend.test",
                tenantId: "T1",
                logger: logger);

            var result = await sut.UploadBatchAsync(Array.Empty<TelemetryItem>(), CancellationToken.None);
            Assert.True(result.Success);
            Assert.Empty(handler.Captured);

            var logContent = ReadLogs(tmp.Path);
            Assert.DoesNotContain("flushing", logContent);
            Assert.DoesNotContain("ingest", logContent);
        }
    }
}
