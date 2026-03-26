export const SECURITY_NAV_SECTIONS = [
  { id: "device-block", label: "Device Block", description: "Block or kill specific devices" },
  { id: "version-block", label: "Version Block", description: "Block agent versions by pattern" },
  { id: "vulnerability-data", label: "Vulnerability Data", description: "NVD/CVE vulnerability data management" },
  { id: "api-keys", label: "API Keys", description: "Manage global and tenant API keys" },
] as const;

export type SecuritySectionId = (typeof SECURITY_NAV_SECTIONS)[number]["id"];
