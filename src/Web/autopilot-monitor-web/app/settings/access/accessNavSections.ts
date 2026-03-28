export const ACCESS_NAV_SECTIONS = [
  { id: "admin-management", label: "Admin Management", description: "Manage tenant admins and operators" },
  { id: "bootstrap-sessions", label: "Bootstrap Sessions", description: "Create and manage bootstrap tokens" },
  { id: "mcp-users", label: "MCP Users", description: "Manage AI agent access via MCP", globalAdminOnly: true },
] as const;

export type AccessSectionId = (typeof ACCESS_NAV_SECTIONS)[number]["id"];
