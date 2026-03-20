using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Fetches rule definitions from GitHub (raw.githubusercontent.com).
    /// Uses the CI-generated combined files from rules/dist/.
    /// Includes retry logic with exponential backoff for transient failures.
    /// </summary>
    public class GitHubRuleRepository
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<GitHubRuleRepository> _logger;
        private readonly string _baseUrl;

        private static readonly TimeSpan[] RetryDelays = new[]
        {
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10)
        };

        public GitHubRuleRepository(HttpClient httpClient, IConfiguration config, ILogger<GitHubRuleRepository> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _baseUrl = config["GitHub:RulesBaseUrl"]
                ?? "https://raw.githubusercontent.com/okieselbach/Autopilot-Monitor/refs/heads/main/rules";
        }

        public async Task<List<GatherRule>> FetchGatherRulesAsync()
        {
            var url = $"{_baseUrl}/dist/gather-rules.json";
            _logger.LogInformation("Fetching gather rules from {Url}", url);
            var json = await FetchWithRetryAsync(url);
            var wrapper = JsonConvert.DeserializeObject<RulesWrapper<GatherRule>>(json);
            var rules = wrapper?.Rules ?? new List<GatherRule>();
            _logger.LogInformation("Fetched {Count} gather rules from GitHub", rules.Count);
            return rules;
        }

        public async Task<List<AnalyzeRule>> FetchAnalyzeRulesAsync()
        {
            var url = $"{_baseUrl}/dist/analyze-rules.json";
            _logger.LogInformation("Fetching analyze rules from {Url}", url);
            var json = await FetchWithRetryAsync(url);
            var wrapper = JsonConvert.DeserializeObject<RulesWrapper<AnalyzeRule>>(json);
            var rules = wrapper?.Rules ?? new List<AnalyzeRule>();
            _logger.LogInformation("Fetched {Count} analyze rules from GitHub", rules.Count);
            return rules;
        }

        public async Task<List<ImeLogPattern>> FetchImeLogPatternsAsync()
        {
            var url = $"{_baseUrl}/dist/ime-log-patterns.json";
            _logger.LogInformation("Fetching IME log patterns from {Url}", url);
            var json = await FetchWithRetryAsync(url);
            var wrapper = JsonConvert.DeserializeObject<RulesWrapper<ImeLogPattern>>(json);
            var patterns = wrapper?.Rules ?? new List<ImeLogPattern>();
            _logger.LogInformation("Fetched {Count} IME log patterns from GitHub", patterns.Count);
            return patterns;
        }

        public async Task<string> FetchCpeCommunityMappingsAsync()
        {
            var url = $"{_baseUrl}/dist/cpe-community-mappings.json";
            _logger.LogInformation("Fetching community CPE mappings from {Url}", url);
            var json = await FetchWithRetryAsync(url);
            _logger.LogInformation("Fetched community CPE mappings JSON from GitHub ({Length} bytes)", json.Length);
            return json;
        }

        /// <summary>
        /// Fetches a URL with retry logic and exponential backoff.
        /// Retries on transient HTTP errors (408, 429, 5xx) and network exceptions.
        /// </summary>
        private async Task<string> FetchWithRetryAsync(string url)
        {
            for (int attempt = 0; attempt <= RetryDelays.Length; attempt++)
            {
                try
                {
                    var response = await _httpClient.GetAsync(url);

                    if (response.IsSuccessStatusCode)
                    {
                        return await response.Content.ReadAsStringAsync();
                    }

                    var statusCode = (int)response.StatusCode;
                    var isTransient = statusCode == 408 || statusCode == 429
                        || statusCode >= 500 && statusCode <= 599;

                    if (!isTransient || attempt >= RetryDelays.Length)
                    {
                        // Non-transient error or retries exhausted — throw to caller
                        var body = await response.Content.ReadAsStringAsync();
                        _logger.LogWarning(
                            "GitHub fetch failed for {Url} after {Attempts} attempt(s). Status: {StatusCode}. Body: {Body}",
                            url, attempt + 1, statusCode, body);
                        response.EnsureSuccessStatusCode(); // Throws HttpRequestException
                    }

                    var delay = RetryDelays[attempt];

                    // Respect Retry-After header (GitHub rate limiting)
                    if (response.Headers.RetryAfter?.Delta is TimeSpan retryAfterDelta && retryAfterDelta > delay)
                    {
                        delay = retryAfterDelta;
                    }

                    _logger.LogWarning(
                        "GitHub fetch for {Url} returned {StatusCode} (attempt {Attempt}/{MaxAttempts}). Retrying in {Delay}s",
                        url, statusCode, attempt + 1, RetryDelays.Length + 1, delay.TotalSeconds);

                    await Task.Delay(delay);
                }
                catch (Exception ex) when (attempt < RetryDelays.Length && (ex is HttpRequestException or TaskCanceledException))
                {
                    var delay = RetryDelays[attempt];
                    _logger.LogWarning(ex,
                        "GitHub fetch for {Url} failed with network error (attempt {Attempt}/{MaxAttempts}). Retrying in {Delay}s",
                        url, attempt + 1, RetryDelays.Length + 1, delay.TotalSeconds);

                    await Task.Delay(delay);
                }
            }

            // Should not reach here, but just in case
            throw new HttpRequestException($"Failed to fetch {url} after {RetryDelays.Length + 1} attempts");
        }
    }
}
