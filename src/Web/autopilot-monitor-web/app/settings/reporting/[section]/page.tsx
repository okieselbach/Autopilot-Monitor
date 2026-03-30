"use client";

import { useParams } from "next/navigation";
import { notFound } from "next/navigation";
import { type ReportingSectionId } from "../reportingNavSections";
import { SectionMcpUsage } from "../sections/SectionMcpUsage";

const SECTION_COMPONENTS: Record<ReportingSectionId, React.ComponentType> = {
  "mcp-usage": SectionMcpUsage,
};

export default function ReportingSectionPage() {
  const params = useParams();
  const section = params.section as string;
  const SectionContent = SECTION_COMPONENTS[section as ReportingSectionId];
  if (!SectionContent) notFound();
  return <SectionContent />;
}
