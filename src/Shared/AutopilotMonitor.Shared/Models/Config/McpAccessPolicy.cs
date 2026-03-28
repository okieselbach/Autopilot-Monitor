namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Controls who can access the remote MCP server.
    /// Stored as string in AdminConfiguration.McpAccessPolicy.
    /// </summary>
    public enum McpAccessPolicy
    {
        /// <summary>MCP access completely disabled.</summary>
        Disabled = 0,

        /// <summary>Only Global Admins and explicitly whitelisted MCP users.</summary>
        WhitelistOnly = 1,

        /// <summary>Any authenticated user with a valid tenant membership.</summary>
        AllMembers = 2
    }
}
