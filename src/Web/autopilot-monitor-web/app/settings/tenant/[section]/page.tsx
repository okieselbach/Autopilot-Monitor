"use client";

import { useParams } from "next/navigation";
import { notFound } from "next/navigation";
import { type TenantSectionId } from "../tenantNavSections";
import { SectionAutopilotValidation } from "../sections/SectionAutopilotValidation";
import { SectionHardwareWhitelist } from "../sections/SectionHardwareWhitelist";
import { SectionNotifications } from "../sections/SectionNotifications";
import { SectionAccessManagement } from "../sections/SectionAccessManagement";
import { SectionBootstrapSessions } from "../sections/SectionBootstrapSessions";
import { SectionSlaTargets } from "../sections/SectionSlaTargets";

const SECTION_COMPONENTS: Record<TenantSectionId, React.ComponentType> = {
  "autopilot": SectionAutopilotValidation,
  "hardware-whitelist": SectionHardwareWhitelist,
  "notifications": SectionNotifications,
  "sla-targets": SectionSlaTargets,
  "access-management": SectionAccessManagement,
  "bootstrap-sessions": SectionBootstrapSessions,
};

export default function TenantSectionPage() {
  const params = useParams();
  const section = params.section as string;
  const SectionContent = SECTION_COMPONENTS[section as TenantSectionId];
  if (!SectionContent) notFound();
  return <SectionContent />;
}
