export const SETTINGS_NAV_SECTIONS = [
  { id: "global", label: "Global Settings", description: "Global platform configuration" },
  { id: "diagnostics-log-paths", label: "Diagnostics Log Paths", description: "Global diagnostics log path configuration" },
  { id: "maintenance", label: "Maintenance", description: "Maintenance triggers and rule reseeding" },
] as const;

export type SettingsSectionId = (typeof SETTINGS_NAV_SECTIONS)[number]["id"];
