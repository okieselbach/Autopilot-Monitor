export const TENANTS_NAV_SECTIONS = [
  { id: "management", label: "Tenant Management", description: "View and manage all tenant configurations" },
  { id: "config-report", label: "Config Report", description: "Detailed tenant configuration report with runtime parameters" },
] as const;

export type TenantsSectionId = (typeof TENANTS_NAV_SECTIONS)[number]["id"];
