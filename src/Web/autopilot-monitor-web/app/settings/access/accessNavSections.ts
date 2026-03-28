export const ACCESS_NAV_SECTIONS = [
  { id: "admin-management", label: "Admin Management", description: "Manage tenant admins and operators" },
  { id: "bootstrap-sessions", label: "Bootstrap Sessions", description: "Create and manage bootstrap tokens" },
] as const;

export type AccessSectionId = (typeof ACCESS_NAV_SECTIONS)[number]["id"];
