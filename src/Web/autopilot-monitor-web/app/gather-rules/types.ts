export interface GatherRule {
  ruleId: string;
  title: string;
  description: string;
  category: string;
  version: string;
  author: string;
  enabled: boolean;
  isBuiltIn: boolean;
  isCommunity: boolean;
  collectorType: string;
  target: string;
  parameters: Record<string, string>;
  trigger: string;
  intervalSeconds: number | null;
  triggerPhase: string | null;
  triggerEventType: string | null;
  outputEventType: string;
  outputSeverity: string;
  tags: string[];
  createdAt: string;
  updatedAt: string;
}

export interface NewRuleForm {
  ruleId: string;
  title: string;
  description: string;
  category: string;
  collectorType: string;
  target: string;
  valueName: string;
  eventId: string;
  messageFilter: string;
  maxEntries: string;
  source: string;
  readContent: boolean;
  logPattern: string;
  trackPosition: boolean;
  maxLines: string;
  jsonPath: string;
  xpath: string;
  xmlNamespaces: string;
  maxResults: string;
  trigger: string;
  intervalSeconds: number;
  triggerPhase: string;
  triggerEventType: string;
  outputEventType: string;
  outputSeverity: string;
}

export const CATEGORIES = ["network", "identity", "apps", "device", "esp", "enrollment"] as const;
export const COLLECTOR_TYPES = ["registry", "eventlog", "wmi", "file", "command_allowlisted", "logparser", "json", "xml"] as const;
export const TRIGGERS = ["startup", "phase_change", "interval", "on_event"] as const;
export const SEVERITIES = ["info", "warning", "error", "critical"] as const;

export const CATEGORY_COLORS: Record<string, { bg: string; text: string }> = {
  network: { bg: "bg-blue-100", text: "text-blue-700" },
  identity: { bg: "bg-purple-100", text: "text-purple-700" },
  apps: { bg: "bg-orange-100", text: "text-orange-700" },
  device: { bg: "bg-gray-100", text: "text-gray-700" },
  esp: { bg: "bg-teal-100", text: "text-teal-700" },
  enrollment: { bg: "bg-indigo-100", text: "text-indigo-700" },
};

export const COLLECTOR_TYPE_LABELS: Record<string, string> = {
  registry: "Registry",
  eventlog: "Event Log",
  wmi: "WMI Query",
  file: "File",
  command_allowlisted: "Command (Allowlisted)",
  command: "Command (Allowlisted)",
  logparser: "Log Parser",
  json: "JSON (JSONPath)",
  xml: "XML (XPath)",
};

export const TARGET_PLACEHOLDERS: Record<string, string> = {
  registry: "e.g., HKLM\\SOFTWARE\\Microsoft\\Enrollments",
  eventlog: "e.g., Microsoft-Windows-Shell-Core/Operational",
  wmi: "e.g., SELECT * FROM Win32_BIOS",
  file: "e.g., C:\\Windows\\Panther\\UnattendGC\\setupact.log",
  command_allowlisted: "e.g., Get-Tpm or dsregcmd /status",
  logparser: "e.g., %ProgramData%\\Microsoft\\IntuneManagementExtension\\Logs\\AppWorkload.log",
  json: "e.g., C:\\ProgramData\\Microsoft\\IntuneManagementExtension\\Logs\\config.json",
  xml: "e.g., C:\\Windows\\Panther\\unattend.xml",
};

export const TARGET_HINTS: Record<string, string> = {
  registry: "Full registry path including hive (HKLM, HKCU). The agent reads values from this key.",
  eventlog: "Event log name — supports operational/analytic logs like Microsoft-Windows-Shell-Core/Operational.",
  wmi: "Full WQL query (SELECT * FROM ...). Must use an allowed WMI class.",
  file: "File path. Environment variables like %ProgramData% are supported. Must be within allowed directories.",
  command_allowlisted: "Exact command string from the agent's allowlist. Custom commands are not permitted.",
  logparser: "Path to a CMTrace-format log file. Environment variables are expanded. Requires a regex pattern in parameters.",
  json: "Path to a JSON file. Environment variables supported. Must be within allowed directories. Use JSONPath to extract values.",
  xml: "Path to an XML file. Environment variables supported. Must be within allowed directories. Use XPath to extract values.",
};

export const EMPTY_FORM: NewRuleForm = {
  ruleId: "",
  title: "",
  description: "",
  category: "device",
  collectorType: "registry",
  target: "",
  valueName: "",
  eventId: "",
  messageFilter: "",
  maxEntries: "",
  source: "",
  readContent: false,
  logPattern: "",
  trackPosition: true,
  maxLines: "",
  jsonPath: "",
  xpath: "",
  xmlNamespaces: "",
  maxResults: "",
  trigger: "startup",
  intervalSeconds: 60,
  triggerPhase: "",
  triggerEventType: "",
  outputEventType: "",
  outputSeverity: "info",
};

export function formatTrigger(trigger: string) {
  switch (trigger) {
    case "phase_change": return "Phase Change";
    case "on_event": return "On Event";
    default: return trigger.charAt(0).toUpperCase() + trigger.slice(1);
  }
}
