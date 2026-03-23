export const MANAGEMENT_NAV_SECTIONS = [
  { id: "data", label: "Data Management", description: "Data retention and session timeout configuration" },
  { id: "offboarding", label: "Offboarding", description: "Tenant offboarding and data removal" },
] as const;

export type ManagementSectionId = (typeof MANAGEMENT_NAV_SECTIONS)[number]["id"];
