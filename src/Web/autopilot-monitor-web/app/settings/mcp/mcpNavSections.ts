export const MCP_NAV_SECTIONS = [
  { id: "usage", label: "Usage", description: "Your MCP API usage statistics" },
] as const;

export type McpSectionId = (typeof MCP_NAV_SECTIONS)[number]["id"];
