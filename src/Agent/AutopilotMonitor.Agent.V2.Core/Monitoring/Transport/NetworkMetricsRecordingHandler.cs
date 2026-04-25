using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Transport
{
    /// <summary>
    /// <see cref="DelegatingHandler"/> that records every outbound HTTP request on a shared
    /// <see cref="NetworkMetrics"/> instance. Inserted at the top of the mTLS handler pipeline
    /// in <see cref="Orchestration.MtlsHttpClientFactory"/> so any component that posts through
    /// the shared <see cref="HttpClient"/> (in particular the <c>BackendTelemetryUploader</c>'s
    /// <c>POST /api/agent/telemetry</c> path, which dominates per-session traffic) is counted.
    /// <para>
    /// Without this handler the legacy <c>BackendApiClient</c> was the only path calling
    /// <see cref="NetworkMetrics.RecordRequest"/> directly, so <c>net_total_requests</c> in
    /// <c>agent_metrics_snapshot</c> only reflected config + gather-rule traffic (~2 / session).
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>bytes-up</b> is read from <see cref="System.Net.Http.Headers.HttpContentHeaders.ContentLength"/>
    /// on the request content (set eagerly by <c>ByteArrayContent</c>/<c>StringContent</c>).
    /// <b>bytes-down</b> is read from the response Content-Length header — when
    /// <c>AutomaticDecompression</c> is enabled the framework may strip this, so the value
    /// is best-effort (degrades to 0). The request count and failure count are always exact.
    /// </remarks>
    internal sealed class NetworkMetricsRecordingHandler : DelegatingHandler
    {
        private readonly NetworkMetrics _metrics;

        public NetworkMetricsRecordingHandler(NetworkMetrics metrics)
        {
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        }

        public NetworkMetricsRecordingHandler(NetworkMetrics metrics, HttpMessageHandler innerHandler)
            : base(innerHandler)
        {
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var bytesUp = request?.Content?.Headers?.ContentLength ?? 0L;
            var sw = Stopwatch.StartNew();
            var failed = false;
            long bytesDown = 0;
            HttpResponseMessage response = null!;

            try
            {
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                bytesDown = response?.Content?.Headers?.ContentLength ?? 0L;
                if (response != null && !response.IsSuccessStatusCode)
                {
                    failed = true;
                }
                return response;
            }
            catch
            {
                failed = true;
                throw;
            }
            finally
            {
                sw.Stop();
                _metrics.RecordRequest(bytesUp, bytesDown, sw.ElapsedMilliseconds, failed);
            }
        }
    }
}
