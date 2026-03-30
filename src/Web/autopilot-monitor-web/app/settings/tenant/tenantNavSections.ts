export const TENANT_NAV_SECTIONS = [
  { id: "autopilot", label: "Autopilot Validation", description: "Autopilot device and corporate identifier validation" },
  { id: "hardware-whitelist", label: "Hardware Whitelist", description: "Manufacturer and model whitelist configuration" },
  { id: "notifications", label: "Notifications", description: "Webhook notification configuration" },
  { id: "access-management", label: "Access Management", description: "Manage tenant admins and operators" },
  { id: "bootstrap-sessions", label: "Bootstrap Sessions", description: "Create and manage bootstrap tokens" },
] as const;

export type TenantSectionId = (typeof TENANT_NAV_SECTIONS)[number]["id"];
