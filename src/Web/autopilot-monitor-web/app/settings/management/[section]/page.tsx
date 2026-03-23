"use client";

import { useParams } from "next/navigation";
import { notFound } from "next/navigation";
import { type ManagementSectionId } from "../managementNavSections";
import { SectionDataManagement } from "../sections/SectionDataManagement";
import { SectionOffboarding } from "../sections/SectionOffboarding";

const SECTION_COMPONENTS: Record<ManagementSectionId, React.ComponentType> = {
  "data": SectionDataManagement,
  "offboarding": SectionOffboarding,
};

export default function ManagementSectionPage() {
  const params = useParams();
  const section = params.section as string;
  const SectionContent = SECTION_COMPONENTS[section as ManagementSectionId];
  if (!SectionContent) notFound();
  return <SectionContent />;
}
