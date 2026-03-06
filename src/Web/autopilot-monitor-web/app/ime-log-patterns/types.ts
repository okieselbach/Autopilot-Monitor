export interface ImeLogPattern {
  patternId: string;
  category: string;
  pattern: string;
  action: string;
  parameters?: Record<string, string>;
  description?: string;
  enabled: boolean;
  isBuiltIn: boolean;
}

export interface PatternForm {
  category: string;
  pattern: string;
  action: string;
  parameters: Record<string, string>;
  description: string;
  enabled: boolean;
}

export const CATEGORIES = ["always", "currentPhase", "otherPhases"] as const;
export const ACTIONS = [
  "setCurrentApp", "updateStateInstalled", "updateStateDownloading",
  "updateStateInstalling", "updateStateSkipped", "updateStateError",
  "updateStatePostponed", "espPhaseDetected", "imeStarted",
  "policiesDiscovered", "ignoreCompletedApp", "imeAgentVersion",
  "espTrackStatus", "updateName", "updateWin32AppState",
  "cancelStuckAndSetCurrent", "imeSessionChange", "imeImpersonation",
  "enrollmentCompleted",
] as const;

export const CATEGORY_COLORS: Record<string, { bg: string; text: string }> = {
  always: { bg: "bg-emerald-100", text: "text-emerald-700" },
  currentPhase: { bg: "bg-blue-100", text: "text-blue-700" },
  otherPhases: { bg: "bg-purple-100", text: "text-purple-700" },
};

export const CATEGORY_LABELS: Record<string, string> = {
  always: "Always",
  currentPhase: "Current Phase",
  otherPhases: "Other Phases",
};

export const ACTION_LABELS: Record<string, string> = {
  setCurrentApp: "Set Current App",
  updateStateInstalled: "State → Installed",
  updateStateDownloading: "State → Downloading",
  updateStateInstalling: "State → Installing",
  updateStateSkipped: "State → Skipped",
  updateStateError: "State → Error",
  updateStatePostponed: "State → Postponed",
  espPhaseDetected: "ESP Phase Detected",
  imeStarted: "IME Started",
  policiesDiscovered: "Policies Discovered",
  ignoreCompletedApp: "Ignore Completed App",
  imeAgentVersion: "IME Agent Version",
  espTrackStatus: "ESP Track Status",
  updateName: "Update Name",
  updateWin32AppState: "Win32 App State",
  cancelStuckAndSetCurrent: "Cancel Stuck & Set Current",
  imeSessionChange: "Session Change",
  imeImpersonation: "IME Impersonation",
  enrollmentCompleted: "Enrollment Completed",
};

export const EMPTY_FORM: PatternForm = {
  category: "always",
  pattern: "",
  action: "setCurrentApp",
  parameters: {},
  description: "",
  enabled: true,
};
