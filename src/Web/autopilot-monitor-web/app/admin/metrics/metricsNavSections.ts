export const METRICS_NAV_SECTIONS = [
  { id: "agent-metrics", label: "Agent Metrics", description: "Agent version distribution, footprints, and detailed breakdown" },
  { id: "usage", label: "Platform Usage", description: "Platform usage statistics across all tenants" },
] as const;

export type MetricsSectionId = (typeof METRICS_NAV_SECTIONS)[number]["id"];
