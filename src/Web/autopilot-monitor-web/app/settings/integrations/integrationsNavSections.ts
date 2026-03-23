export const INTEGRATIONS_NAV_SECTIONS = [
  { id: "notifications", label: "Notifications", description: "Webhook notification configuration" },
  { id: "diagnostics", label: "Diagnostics", description: "Diagnostics upload and log path configuration" },
] as const;

export type IntegrationsSectionId = (typeof INTEGRATIONS_NAV_SECTIONS)[number]["id"];
