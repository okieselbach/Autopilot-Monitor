// Phase definitions per enrollment type
export const V1_PHASES = [
  { id: 0, name: "Start",               shortName: "Start" },
  { id: 1, name: "Device Preparation",  shortName: "Device Preparation" },
  { id: 2, name: "Device Setup",        shortName: "Device Setup" },
  { id: 3, name: "Apps (Device)",       shortName: "Apps (Device)" },
  { id: 4, name: "Account Setup",       shortName: "Account Setup" },
  { id: 5, name: "Apps (User)",         shortName: "Apps (User)" },
  { id: 6, name: "Finalizing Setup",    shortName: "Finalizing" },
  { id: 7, name: "Complete",            shortName: "Complete" },
];

export const V2_PHASES = [
  { id: 0, name: "Start",               shortName: "Start" },
  { id: 1, name: "Device Preparation",  shortName: "Device Preparation" },
  { id: 3, name: "App Installation",    shortName: "Apps" },
  { id: 6, name: "Finalizing Setup",    shortName: "Finalizing" },
  { id: 7, name: "Complete",            shortName: "Complete" },
];

export const V1_PHASE_ORDER = ["Start", "Device Preparation", "Device Setup", "Apps (Device)", "Account Setup", "Apps (User)", "Finalizing Setup", "Complete", "Failed"];
export const V2_PHASE_ORDER = ["Start", "Device Preparation", "App Installation", "Finalizing Setup", "Complete", "Failed"];

// Lookup by phase number — Phase 3 has different names per enrollment type
export const V1_PHASE_NAMES: Record<number, string> = { [-1]: "Unknown", 0: "Start", 1: "Device Preparation", 2: "Device Setup", 3: "Apps (Device)",    4: "Account Setup", 5: "Apps (User)", 6: "Finalizing Setup", 7: "Complete", 99: "Failed" };
export const V2_PHASE_NAMES: Record<number, string> = { [-1]: "Unknown", 0: "Start", 1: "Device Preparation", 2: "Device Setup", 3: "App Installation", 4: "Account Setup", 5: "Apps (User)", 6: "Finalizing Setup", 7: "Complete", 99: "Failed" };
