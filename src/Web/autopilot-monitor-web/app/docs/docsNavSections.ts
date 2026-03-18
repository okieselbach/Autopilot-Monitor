export const NAV_SECTIONS = [
  { id: "private-preview", label: "Private Preview",   description: "Private preview access and onboarding for Autopilot Monitor." },
  { id: "overview",        label: "Overview",          description: "Overview of Autopilot Monitor architecture and capabilities." },
  { id: "general",         label: "General",            description: "General concepts: Admin Mode, session statuses, and user roles." },
  { id: "setup",           label: "Setup",             description: "Step-by-step setup guide for deploying Autopilot Monitor." },
  { id: "agent",           label: "Agent",              description: "Autopilot Monitor agent overview, version, and configuration details." },
  { id: "agent-setup",     label: "Agent Setup",       description: "Configure and deploy the Autopilot Monitor agent via Intune." },
  { id: "settings",        label: "Settings",          description: "Autopilot Monitor settings and configuration options." },
  { id: "gather-rules",    label: "Gather Rules",      description: "Configure data gathering rules for Autopilot Monitor diagnostics." },
  { id: "analyze-rules",   label: "Analyze Rules",     description: "Configure analysis rules for Autopilot enrollment evaluation." },
  { id: "ime-log-patterns", label: "IME Log Patterns", description: "Regex patterns for parsing the Intune Management Extension log into structured events." },
] as const;

export type SectionId = (typeof NAV_SECTIONS)[number]["id"];
