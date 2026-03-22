"use client";

import { useParams } from "next/navigation";
import { notFound } from "next/navigation";
import { type MetricsSectionId } from "../metricsNavSections";
import { SectionAgentMetrics } from "../sections/SectionAgentMetrics";
import { SectionPlatformUsage } from "../sections/SectionPlatformUsage";

const SECTION_COMPONENTS: Record<MetricsSectionId, React.ComponentType> = {
  "agent-metrics": SectionAgentMetrics,
  "usage": SectionPlatformUsage,
};

export default function MetricsSectionPage() {
  const params = useParams();
  const section = params.section as string;
  const SectionContent = SECTION_COMPONENTS[section as MetricsSectionId];
  if (!SectionContent) notFound();
  return <SectionContent />;
}
