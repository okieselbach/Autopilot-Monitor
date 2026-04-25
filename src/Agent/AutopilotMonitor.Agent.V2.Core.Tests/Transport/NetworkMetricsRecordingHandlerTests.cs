using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Transport
{
    /// <summary>
    /// Unit-tests for the <see cref="NetworkMetricsRecordingHandler"/> — the piece that wires
    /// the V2 telemetry-upload path into <c>NetworkMetrics</c> so <c>net_total_requests</c>
    /// in <c>agent_metrics_snapshot</c> reflects every outbound HTTP call (not just the
    /// legacy BackendApiClient ones).
    /// </summary>
    public sealed class NetworkMetricsRecordingHandlerTests
    {
        private static HttpClient BuildClient(NetworkMetrics metrics, RecordingHttpMessageHandler inner)
        {
            var pipeline = new NetworkMetricsRecordingHandler(metrics, inner);
            return new HttpClient(pipeline);
        }

        private static HttpRequestMessage NewPost(string body)
        {
            return new HttpRequestMessage(HttpMethod.Post, "https://backend.test/api/agent/telemetry")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }

        [Fact]
        public async Task Records_one_request_on_2xx()
        {
            var metrics = new NetworkMetrics();
            var inner = new RecordingHttpMessageHandler();
            inner.QueueStatus(HttpStatusCode.OK, body: "{}");
            using var client = BuildClient(metrics, inner);

            using var resp = await client.SendAsync(NewPost("hello"));

            var snap = metrics.GetSnapshot();
            Assert.Equal(1, snap.RequestCount);
            Assert.Equal(0, snap.FailureCount);
        }

        [Fact]
        public async Task Records_failure_on_5xx()
        {
            var metrics = new NetworkMetrics();
            var inner = new RecordingHttpMessageHandler();
            inner.QueueStatus(HttpStatusCode.InternalServerError);
            using var client = BuildClient(metrics, inner);

            using var resp = await client.SendAsync(NewPost("x"));

            var snap = metrics.GetSnapshot();
            Assert.Equal(1, snap.RequestCount);
            Assert.Equal(1, snap.FailureCount);
        }

        [Fact]
        public async Task Records_failure_on_4xx()
        {
            // Legacy BackendApiClient surfaces 4xx as exceptions via EnsureSuccessStatusCode
            // which is then counted as failed in its finally block. The handler runs upstream
            // of EnsureSuccessStatusCode, so it sees the response directly — non-2xx is failed.
            var metrics = new NetworkMetrics();
            var inner = new RecordingHttpMessageHandler();
            inner.QueueStatus(HttpStatusCode.Unauthorized);
            using var client = BuildClient(metrics, inner);

            using var resp = await client.SendAsync(NewPost("x"));

            var snap = metrics.GetSnapshot();
            Assert.Equal(1, snap.RequestCount);
            Assert.Equal(1, snap.FailureCount);
        }

        [Fact]
        public async Task Records_failure_on_inner_exception_and_rethrows()
        {
            var metrics = new NetworkMetrics();
            var inner = new RecordingHttpMessageHandler();
            inner.QueueThrow(new HttpRequestException("network down"));
            using var client = BuildClient(metrics, inner);

            await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(NewPost("x")));

            var snap = metrics.GetSnapshot();
            Assert.Equal(1, snap.RequestCount);
            Assert.Equal(1, snap.FailureCount);
        }

        [Fact]
        public async Task Captures_bytes_up_from_request_content_length()
        {
            var metrics = new NetworkMetrics();
            var inner = new RecordingHttpMessageHandler();
            inner.QueueStatus(HttpStatusCode.OK);
            using var client = BuildClient(metrics, inner);

            var body = "0123456789"; // 10 bytes
            using var resp = await client.SendAsync(NewPost(body));

            var snap = metrics.GetSnapshot();
            Assert.Equal(1, snap.RequestCount);
            Assert.Equal(10, snap.TotalBytesUp);
        }

        [Fact]
        public async Task Captures_bytes_down_from_response_content_length()
        {
            var metrics = new NetworkMetrics();
            var inner = new RecordingHttpMessageHandler();
            inner.QueueStatus(HttpStatusCode.OK, body: "abcd"); // 4 bytes
            using var client = BuildClient(metrics, inner);

            using var resp = await client.SendAsync(NewPost(""));

            var snap = metrics.GetSnapshot();
            Assert.Equal(1, snap.RequestCount);
            Assert.Equal(4, snap.TotalBytesDown);
        }

        [Fact]
        public async Task Records_each_request_separately_for_multi_send()
        {
            var metrics = new NetworkMetrics();
            var inner = new RecordingHttpMessageHandler();
            inner.QueueStatus(HttpStatusCode.OK);
            inner.QueueStatus(HttpStatusCode.InternalServerError);
            inner.QueueStatus(HttpStatusCode.OK);
            using var client = BuildClient(metrics, inner);

            using (var r1 = await client.SendAsync(NewPost("a"))) { }
            using (var r2 = await client.SendAsync(NewPost("bb"))) { }
            using (var r3 = await client.SendAsync(NewPost("ccc"))) { }

            var snap = metrics.GetSnapshot();
            Assert.Equal(3, snap.RequestCount);
            Assert.Equal(1, snap.FailureCount);
            Assert.Equal(1 + 2 + 3, snap.TotalBytesUp);
        }

        [Fact]
        public void Constructor_rejects_null_metrics()
        {
            Assert.Throws<ArgumentNullException>(() => new NetworkMetricsRecordingHandler(null!));
            Assert.Throws<ArgumentNullException>(
                () => new NetworkMetricsRecordingHandler(null!, new RecordingHttpMessageHandler()));
        }
    }
}
