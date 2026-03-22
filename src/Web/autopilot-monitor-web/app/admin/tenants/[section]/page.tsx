"use client";

import { useParams, notFound } from "next/navigation";
import { type TenantsSectionId } from "../tenantsNavSections";
import { SectionTenantManagement } from "../sections/SectionTenantManagement";
import { SectionTenantConfigReport } from "../sections/SectionTenantConfigReport";

const SECTION_COMPONENTS: Record<TenantsSectionId, React.ComponentType> = {
  "management": SectionTenantManagement,
  "config-report": SectionTenantConfigReport,
};

export default function TenantsSectionPage() {
  const params = useParams();
  const section = params.section as string;
  const SectionContent = SECTION_COMPONENTS[section as TenantsSectionId];
  if (!SectionContent) notFound();
  return <SectionContent />;
}
