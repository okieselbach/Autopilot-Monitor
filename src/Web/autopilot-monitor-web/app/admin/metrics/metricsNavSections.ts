export const METRICS_NAV_SECTIONS = [
  { id: "agent-metrics", label: "Agent Metrics", description: "Agent version distribution, footprints, and detailed breakdown" },
  { id: "usage", label: "Platform Usage", description: "Platform usage statistics across all tenants" },
  { id: "mcp-usage", label: "MCP Usage", description: "MCP API usage metrics across all users" },
] as const;

export type MetricsSectionId = (typeof METRICS_NAV_SECTIONS)[number]["id"];
