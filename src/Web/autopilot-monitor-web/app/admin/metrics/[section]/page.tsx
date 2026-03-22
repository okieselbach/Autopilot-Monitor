"use client";

import { useParams } from "next/navigation";
import { notFound } from "next/navigation";
import { METRICS_NAV_SECTIONS, type MetricsSectionId } from "../metricsNavSections";
import { SectionPlatformMetrics } from "../sections/SectionPlatformMetrics";
import { SectionPlatformUsage } from "../sections/SectionPlatformUsage";

const SECTION_COMPONENTS: Record<MetricsSectionId, React.ComponentType> = {
  "platform-metrics": SectionPlatformMetrics,
  "platform-usage": SectionPlatformUsage,
};

export default function MetricsSectionPage() {
  const params = useParams();
  const section = params.section as string;
  const SectionContent = SECTION_COMPONENTS[section as MetricsSectionId];
  if (!SectionContent) notFound();
  return <SectionContent />;
}
