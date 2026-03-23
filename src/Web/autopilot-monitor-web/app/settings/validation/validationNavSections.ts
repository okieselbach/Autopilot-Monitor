export const VALIDATION_NAV_SECTIONS = [
  { id: "autopilot", label: "Autopilot Validation", description: "Autopilot device and corporate identifier validation" },
  { id: "hardware-whitelist", label: "Hardware Whitelist", description: "Manufacturer and model whitelist configuration" },
] as const;

export type ValidationSectionId = (typeof VALIDATION_NAV_SECTIONS)[number]["id"];
