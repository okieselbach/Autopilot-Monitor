using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AutopilotMonitor.Functions.Tests.GraphResolution;

/// <summary>
/// Minimal scripted HTTP handler for resolver tests. URL is matched as a substring so callers
/// can scope by route fragment (e.g. "deviceManagementScripts/abc" matches the per-ID GET).
/// First match wins; un-scripted URLs throw to force the test to declare every expected call.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly List<(string UrlSubstring, HttpStatusCode Status, string Body)> _scripted = new();
    public List<string> Requests { get; } = new();

    public StubHttpMessageHandler When(string urlSubstring, HttpStatusCode status, string body)
    {
        _scripted.Add((urlSubstring, status, body));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Requests.Add(url);
        var match = _scripted.FirstOrDefault(s => url.Contains(s.UrlSubstring, StringComparison.OrdinalIgnoreCase));
        if (match.Equals(default((string, HttpStatusCode, string))))
        {
            throw new InvalidOperationException($"StubHttpMessageHandler: unscripted URL {url}");
        }
        return Task.FromResult(new HttpResponseMessage(match.Status)
        {
            Content = new StringContent(match.Body),
        });
    }
}

/// <summary>Tiny <see cref="IHttpClientFactory"/> that always returns a client wired to a stub handler.</summary>
internal sealed class StubHttpClientFactory : System.Net.Http.IHttpClientFactory
{
    private readonly StubHttpMessageHandler _handler;
    public StubHttpClientFactory(StubHttpMessageHandler handler) => _handler = handler;
    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}
