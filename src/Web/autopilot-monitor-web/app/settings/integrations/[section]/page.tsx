"use client";

import { useParams } from "next/navigation";
import { notFound } from "next/navigation";
import { type IntegrationsSectionId } from "../integrationsNavSections";
import { SectionNotifications } from "../sections/SectionNotifications";
import { SectionDiagnostics } from "../sections/SectionDiagnostics";

const SECTION_COMPONENTS: Record<IntegrationsSectionId, React.ComponentType> = {
  "notifications": SectionNotifications,
  "diagnostics": SectionDiagnostics,
};

export default function IntegrationsSectionPage() {
  const params = useParams();
  const section = params.section as string;
  const SectionContent = SECTION_COMPONENTS[section as IntegrationsSectionId];
  if (!SectionContent) notFound();
  return <SectionContent />;
}
