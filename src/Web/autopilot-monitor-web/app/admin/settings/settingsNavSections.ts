export const SETTINGS_NAV_SECTIONS = [
  { id: "global", label: "Global Settings", description: "Global platform configuration" },
  { id: "diagnostics-log-paths", label: "Diagnostics Log Paths", description: "Global diagnostics log path configuration" },
  { id: "mcp-users", label: "MCP Users", description: "Manage AI agent access via MCP" },
  { id: "config-reseed", label: "Config Reseed", description: "Fetch and reseed rules from GitHub" },
] as const;

export type SettingsSectionId = (typeof SETTINGS_NAV_SECTIONS)[number]["id"];
