export const METRICS_NAV_SECTIONS = [
  { id: "platform-metrics", label: "Platform Metrics", description: "Platform-wide metrics dashboard" },
  { id: "platform-usage", label: "Platform Usage", description: "Platform usage statistics across all tenants" },
] as const;

export type MetricsSectionId = (typeof METRICS_NAV_SECTIONS)[number]["id"];
