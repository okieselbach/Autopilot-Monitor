"use client";

import { useParams } from "next/navigation";
import { notFound } from "next/navigation";
import { type SettingsSectionId } from "../settingsNavSections";
import { SectionGlobalSettings } from "../sections/SectionGlobalSettings";
import { SectionDiagnosticsLogPaths } from "../sections/SectionDiagnosticsLogPaths";
import { SectionMcpUsers } from "../sections/SectionMcpUsers";
import { SectionConfigReseed } from "../sections/SectionConfigReseed";

const SECTION_COMPONENTS: Record<SettingsSectionId, React.ComponentType> = {
  "global": SectionGlobalSettings,
  "diagnostics-log-paths": SectionDiagnosticsLogPaths,
  "mcp-users": SectionMcpUsers,
  "config-reseed": SectionConfigReseed,
};

export default function SettingsSectionPage() {
  const params = useParams();
  const section = params.section as string;
  const SectionContent = SECTION_COMPONENTS[section as SettingsSectionId];
  if (!SectionContent) notFound();
  return <SectionContent />;
}
