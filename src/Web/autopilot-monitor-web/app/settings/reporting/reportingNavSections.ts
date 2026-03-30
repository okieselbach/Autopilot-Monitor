export const REPORTING_NAV_SECTIONS = [
  { id: "mcp-usage", label: "MCP Usage", description: "Your MCP API usage statistics" },
] as const;

export type ReportingSectionId = (typeof REPORTING_NAV_SECTIONS)[number]["id"];
