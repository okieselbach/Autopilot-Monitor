"use client";

import { useParams } from "next/navigation";
import { notFound } from "next/navigation";
import { VALIDATION_NAV_SECTIONS, type ValidationSectionId } from "../validationNavSections";
import { SectionAutopilotValidation } from "../sections/SectionAutopilotValidation";
import { SectionHardwareWhitelist } from "../sections/SectionHardwareWhitelist";

const SECTION_COMPONENTS: Record<ValidationSectionId, React.ComponentType> = {
  "autopilot": SectionAutopilotValidation,
  "hardware-whitelist": SectionHardwareWhitelist,
};

export default function ValidationSectionPage() {
  const params = useParams();
  const section = params.section as string;
  const SectionContent = SECTION_COMPONENTS[section as ValidationSectionId];
  if (!SectionContent) notFound();
  return <SectionContent />;
}
