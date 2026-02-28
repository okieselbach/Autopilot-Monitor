using System.Collections.Generic;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Wrapper for the combined JSON format: { "$schema": "...", "rules": [...] }
    /// Used for both embedded resources and GitHub-fetched rule files.
    /// </summary>
    public class RulesWrapper<T>
    {
        [JsonProperty("rules")]
        public List<T> Rules { get; set; } = new List<T>();
    }
}
