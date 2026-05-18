namespace AutopilotMonitor.Shared.Models.Graph
{
    /// <summary>
    /// Constant catalog of Microsoft Graph application permission names referenced by the
    /// Autopilot Monitor backend. Centralised so feature toggles, the customer-side grant
    /// script, and the runtime permission detector cannot drift apart.
    /// </summary>
    public static class GraphAppPermissions
    {
        /// <summary>Read display names + file names of Intune device management scripts (Platform Scripts + Remediations).</summary>
        public const string DeviceManagementScriptsReadAll = "DeviceManagementScripts.Read.All";

        /// <summary>Read Intune configuration profiles (future use — policy display names).</summary>
        public const string DeviceManagementConfigurationReadAll = "DeviceManagementConfiguration.Read.All";

        /// <summary>Read Intune managed apps (future use — Win32 app display names not yet covered).</summary>
        public const string DeviceManagementAppsReadAll = "DeviceManagementApps.Read.All";
    }
}
