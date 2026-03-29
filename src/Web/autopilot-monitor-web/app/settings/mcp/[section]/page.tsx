"use client";

import { useParams } from "next/navigation";
import { notFound } from "next/navigation";
import { type McpSectionId } from "../mcpNavSections";
import { SectionMcpUsage } from "../sections/SectionMcpUsage";

const SECTION_COMPONENTS: Record<McpSectionId, React.ComponentType> = {
  "usage": SectionMcpUsage,
};

export default function McpSectionPage() {
  const params = useParams();
  const section = params.section as string;
  const SectionContent = SECTION_COMPONENTS[section as McpSectionId];
  if (!SectionContent) notFound();
  return <SectionContent />;
}
