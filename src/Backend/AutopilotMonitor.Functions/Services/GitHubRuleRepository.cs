using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Fetches rule definitions from GitHub (raw.githubusercontent.com).
    /// Uses the CI-generated combined files from rules/dist/.
    /// </summary>
    public class GitHubRuleRepository
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<GitHubRuleRepository> _logger;
        private readonly string _baseUrl;

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
            _logger.LogInformation($"Fetching gather rules from {url}");
            var json = await _httpClient.GetStringAsync(url);
            var wrapper = JsonConvert.DeserializeObject<RulesWrapper<GatherRule>>(json);
            var rules = wrapper?.Rules ?? new List<GatherRule>();
            _logger.LogInformation($"Fetched {rules.Count} gather rules from GitHub");
            return rules;
        }

        public async Task<List<AnalyzeRule>> FetchAnalyzeRulesAsync()
        {
            var url = $"{_baseUrl}/dist/analyze-rules.json";
            _logger.LogInformation($"Fetching analyze rules from {url}");
            var json = await _httpClient.GetStringAsync(url);
            var wrapper = JsonConvert.DeserializeObject<RulesWrapper<AnalyzeRule>>(json);
            var rules = wrapper?.Rules ?? new List<AnalyzeRule>();
            _logger.LogInformation($"Fetched {rules.Count} analyze rules from GitHub");
            return rules;
        }

        public async Task<List<ImeLogPattern>> FetchImeLogPatternsAsync()
        {
            var url = $"{_baseUrl}/dist/ime-log-patterns.json";
            _logger.LogInformation($"Fetching IME log patterns from {url}");
            var json = await _httpClient.GetStringAsync(url);
            var wrapper = JsonConvert.DeserializeObject<RulesWrapper<ImeLogPattern>>(json);
            var patterns = wrapper?.Rules ?? new List<ImeLogPattern>();
            _logger.LogInformation($"Fetched {patterns.Count} IME log patterns from GitHub");
            return patterns;
        }
    }
}
