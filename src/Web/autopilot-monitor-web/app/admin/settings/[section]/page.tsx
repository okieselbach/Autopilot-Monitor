"use client";

import { useParams } from "next/navigation";
import { notFound } from "next/navigation";
import { SETTINGS_NAV_SECTIONS, type SettingsSectionId } from "../settingsNavSections";
import { SectionGlobalSettings } from "../sections/SectionGlobalSettings";
import { SectionDiagnosticsLogPaths } from "../sections/SectionDiagnosticsLogPaths";
import { SectionMaintenance } from "../sections/SectionMaintenance";

const SECTION_COMPONENTS: Record<SettingsSectionId, React.ComponentType> = {
  "global": SectionGlobalSettings,
  "diagnostics-log-paths": SectionDiagnosticsLogPaths,
  "maintenance": SectionMaintenance,
};

export default function SettingsSectionPage() {
  const params = useParams();
  const section = params.section as string;
  const SectionContent = SECTION_COMPONENTS[section as SettingsSectionId];
  if (!SectionContent) notFound();
  return <SectionContent />;
}
