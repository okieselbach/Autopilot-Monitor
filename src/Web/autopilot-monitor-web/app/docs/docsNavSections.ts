interface NavSection {
  readonly id: string;
  readonly label: string;
  readonly description: string;
  readonly requiresMcpAccess?: true;
  readonly hidden?: true;
}

export const NAV_SECTIONS: readonly NavSection[] = [
  { id: "private-preview", label: "Private Preview",   description: "Join the Autopilot Monitor private preview for early access. Learn about onboarding steps, requirements, and how to start monitoring Windows Autopilot enrollments." },
  { id: "overview",        label: "Overview",          description: "Explore the Autopilot Monitor architecture and core components. Learn how the agent, backend, and web dashboard work together to monitor Windows Autopilot enrollments." },
  { id: "general",         label: "General",            description: "Learn general Autopilot Monitor concepts including Admin Mode, session lifecycle statuses, user roles, and how enrollment data flows through the platform." },
  { id: "setup",           label: "Setup",             description: "Step-by-step guide to setting up Autopilot Monitor. Deploy the bootstrapper package via Microsoft Intune and start monitoring Windows Autopilot enrollments." },
  { id: "agent",           label: "Agent",              description: "Understand the Autopilot Monitor agent — how it collects enrollment data, communicates with the backend, and provides real-time visibility into Windows deployments." },
  { id: "agent-setup",     label: "Agent Setup",       description: "Configure and deploy the Autopilot Monitor agent via Microsoft Intune. Covers bootstrapper deployment, agent configuration, and verification of installation." },
  { id: "settings",        label: "Settings",          description: "Configure Autopilot Monitor settings including tenant options, diagnostics, notification preferences, and advanced platform options for Windows Autopilot monitoring." },
  { id: "gather-rules",    label: "Gather Rules",      description: "Configure gather rules to control which diagnostics data the Autopilot Monitor agent collects. Define custom log paths and collection criteria for troubleshooting." },
  { id: "analyze-rules",   label: "Analyze Rules",     description: "Set up analyze rules to automatically evaluate Windows Autopilot enrollments. Define conditions, severity levels, and custom logic to detect issues in real time." },
  { id: "ime-log-patterns", label: "IME Log Patterns", description: "Define regex patterns for parsing the Intune Management Extension (IME) log into structured events. Customize how Autopilot Monitor extracts log entries." },
  { id: "faq",              label: "FAQ",              description: "Frequently asked questions about Autopilot Monitor — covering setup, agent behavior, troubleshooting common issues, and tips for getting the most out of the platform." },
  { id: "known-issues",    label: "Known Issues",     description: "Documented issues caused by external changes that affect Autopilot Monitor — breaking changes, known limitations, workarounds, and current status." },
  { id: "mcp-integration", label: "MCP Integration",  description: "Connect AI assistants to Autopilot Monitor via Model Context Protocol. Configure MCP clients, explore available tools, and learn how to query enrollment data with natural language.", requiresMcpAccess: true },
  { id: "agent-changelog", label: "Agent Changelog",  description: "Changelog of user-facing changes to the Autopilot Monitor agent — new features, behavior changes, and improvements over time.", hidden: true },
] as const;

export type SectionId = NavSection["id"];
