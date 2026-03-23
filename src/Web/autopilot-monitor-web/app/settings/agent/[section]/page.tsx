"use client";

import { useParams } from "next/navigation";
import { notFound } from "next/navigation";
import { type AgentSectionId } from "../agentNavSections";
import { SectionAgentSettings } from "../sections/SectionAgentSettings";
import { SectionAgentAnalyzers } from "../sections/SectionAgentAnalyzers";
import { SectionUnrestrictedMode } from "../sections/SectionUnrestrictedMode";

const SECTION_COMPONENTS: Record<AgentSectionId, React.ComponentType> = {
  "settings": SectionAgentSettings,
  "analyzers": SectionAgentAnalyzers,
  "unrestricted-mode": SectionUnrestrictedMode,
};

export default function AgentSectionPage() {
  const params = useParams();
  const section = params.section as string;
  const SectionContent = SECTION_COMPONENTS[section as AgentSectionId];
  if (!SectionContent) notFound();
  return <SectionContent />;
}
