using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Security;
using AutopilotMonitor.Agent.Core.Tests.Helpers;
using Xunit;

namespace AutopilotMonitor.Agent.Core.Tests.Security
{
    public class BootstrapConfigCleanupTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _bootstrapPath;

        public BootstrapConfigCleanupTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "ap-bootstrap-cleanup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _bootstrapPath = Path.Combine(_tempDir, "bootstrap-config.json");
            File.WriteAllText(_bootstrapPath, "{\"BootstrapToken\":\"secret\"}");
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
            catch { /* best effort */ }
        }

        private static HttpClient BuildClient(FakeHttpMessageHandler handler)
            => new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

        private static HttpRequestMessage BuildRequest()
            => new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/api/agent/config");

        [Fact]
        public async Task Probe_200_DeletesFile()
        {
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            using (var http = BuildClient(handler))
            {
                await BootstrapConfigCleanup.ProbeAndDeleteCoreAsync(
                    _bootstrapPath, http, BuildRequest(), TestLogger.Instance);
            }

            Assert.False(File.Exists(_bootstrapPath), "bootstrap-config.json should have been deleted after successful probe");
        }

        [Fact]
        public async Task Probe_204_DeletesFile()
        {
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
            using (var http = BuildClient(handler))
            {
                await BootstrapConfigCleanup.ProbeAndDeleteCoreAsync(
                    _bootstrapPath, http, BuildRequest(), TestLogger.Instance);
            }

            Assert.False(File.Exists(_bootstrapPath), "2xx responses must count as success");
        }

        [Fact]
        public async Task Probe_401_KeepsFile()
        {
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
            using (var http = BuildClient(handler))
            {
                await BootstrapConfigCleanup.ProbeAndDeleteCoreAsync(
                    _bootstrapPath, http, BuildRequest(), TestLogger.Instance);
            }

            Assert.True(File.Exists(_bootstrapPath), "401 must keep the bootstrap file for retry next run");
        }

        [Fact]
        public async Task Probe_500_KeepsFile()
        {
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
            using (var http = BuildClient(handler))
            {
                await BootstrapConfigCleanup.ProbeAndDeleteCoreAsync(
                    _bootstrapPath, http, BuildRequest(), TestLogger.Instance);
            }

            Assert.True(File.Exists(_bootstrapPath), "5xx must keep the bootstrap file");
        }

        [Fact]
        public async Task Probe_NetworkError_KeepsFile_DoesNotThrow()
        {
            var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("simulated connection refused"));
            using (var http = BuildClient(handler))
            {
                await BootstrapConfigCleanup.ProbeAndDeleteCoreAsync(
                    _bootstrapPath, http, BuildRequest(), TestLogger.Instance);
            }

            Assert.True(File.Exists(_bootstrapPath), "network failure must keep the file and not throw");
        }

        [Fact]
        public async Task Probe_200_ButFileAlreadyGone_DoesNotThrow()
        {
            File.Delete(_bootstrapPath);
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            using (var http = BuildClient(handler))
            {
                await BootstrapConfigCleanup.ProbeAndDeleteCoreAsync(
                    _bootstrapPath, http, BuildRequest(), TestLogger.Instance);
            }

            Assert.False(File.Exists(_bootstrapPath));
        }

        private sealed class FakeHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

            public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            {
                _responder = responder;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                try
                {
                    return Task.FromResult(_responder(request));
                }
                catch (Exception ex)
                {
                    var tcs = new TaskCompletionSource<HttpResponseMessage>();
                    tcs.SetException(ex);
                    return tcs.Task;
                }
            }
        }
    }
}
