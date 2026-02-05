using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Logging;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Agent.Core.Network
{
    public class GeoLocationResult
    {
        public string Country { get; set; }
        public string Region { get; set; }
        public string City { get; set; }
        public string Loc { get; set; }
        public string Timezone { get; set; }
        public string Source { get; set; }

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                { "country", Country },
                { "region", Region },
                { "city", City },
                { "loc", Loc },
                { "timezone", Timezone },
                { "source", Source }
            };
        }
    }

    public static class GeoLocationService
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

        public static async Task<GeoLocationResult> GetLocationAsync(AgentLogger logger)
        {
            // Try ipinfo.io first
            var result = await TryIpInfo(logger);
            if (result != null) return result;

            // Fallback to ifconfig.co
            result = await TryIfConfigCo(logger);
            if (result != null) return result;

            logger?.Warning("GeoLocation: All providers failed, skipping location event");
            return null;
        }

        private static async Task<GeoLocationResult> TryIpInfo(AgentLogger logger)
        {
            try
            {
                logger?.Info("GeoLocation: Querying ipinfo.io...");

                using (var client = new HttpClient { Timeout = RequestTimeout })
                {
                    client.DefaultRequestHeaders.Add("Accept", "application/json");
                    var response = await client.GetStringAsync("https://ipinfo.io/json");
                    var json = JObject.Parse(response);

                    var result = new GeoLocationResult
                    {
                        Country = json.Value<string>("country"),
                        Region = json.Value<string>("region"),
                        City = json.Value<string>("city"),
                        Loc = json.Value<string>("loc"),
                        Timezone = json.Value<string>("timezone"),
                        Source = "ipinfo"
                    };

                    logger?.Info($"GeoLocation: ipinfo.io returned {result.City}, {result.Region}, {result.Country}");
                    return result;
                }
            }
            catch (Exception ex)
            {
                logger?.Warning($"GeoLocation: ipinfo.io failed: {ex.Message}");
                return null;
            }
        }

        private static async Task<GeoLocationResult> TryIfConfigCo(AgentLogger logger)
        {
            try
            {
                logger?.Info("GeoLocation: Falling back to ifconfig.co...");

                using (var client = new HttpClient { Timeout = RequestTimeout })
                {
                    client.DefaultRequestHeaders.Add("Accept", "application/json");
                    var response = await client.GetStringAsync("https://ifconfig.co/json");
                    var json = JObject.Parse(response);

                    var latitude = json.Value<string>("latitude") ?? "";
                    var longitude = json.Value<string>("longitude") ?? "";
                    var loc = !string.IsNullOrEmpty(latitude) && !string.IsNullOrEmpty(longitude)
                        ? $"{latitude},{longitude}"
                        : "";

                    var result = new GeoLocationResult
                    {
                        Country = json.Value<string>("country_iso"),
                        Region = json.Value<string>("region_name"),
                        City = json.Value<string>("city"),
                        Loc = loc,
                        Timezone = json.Value<string>("time_zone"),
                        Source = "ifconfig.co"
                    };

                    logger?.Info($"GeoLocation: ifconfig.co returned {result.City}, {result.Region}, {result.Country}");
                    return result;
                }
            }
            catch (Exception ex)
            {
                logger?.Warning($"GeoLocation: ifconfig.co failed: {ex.Message}");
                return null;
            }
        }
    }
}
