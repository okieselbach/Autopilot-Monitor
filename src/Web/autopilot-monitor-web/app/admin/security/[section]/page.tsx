"use client";

import { useParams } from "next/navigation";
import { notFound } from "next/navigation";
import { SECURITY_NAV_SECTIONS, type SecuritySectionId } from "../securityNavSections";
import { SectionDeviceBlock } from "../sections/SectionDeviceBlock";
import { SectionVersionBlock } from "../sections/SectionVersionBlock";
import { SectionVulnerabilityData } from "../sections/SectionVulnerabilityData";
import { SectionApiKeys } from "../sections/SectionApiKeys";

const SECTION_COMPONENTS: Record<SecuritySectionId, React.ComponentType> = {
  "device-block": SectionDeviceBlock,
  "version-block": SectionVersionBlock,
  "vulnerability-data": SectionVulnerabilityData,
  "api-keys": SectionApiKeys,
};

export default function SecuritySectionPage() {
  const params = useParams();
  const section = params.section as string;
  const SectionContent = SECTION_COMPONENTS[section as SecuritySectionId];
  if (!SectionContent) notFound();
  return <SectionContent />;
}
