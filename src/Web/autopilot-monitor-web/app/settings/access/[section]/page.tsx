"use client";

import { useParams } from "next/navigation";
import { notFound } from "next/navigation";
import { type AccessSectionId } from "../accessNavSections";
import { SectionAdminManagement } from "../sections/SectionAdminManagement";
import { SectionBootstrapSessions } from "../sections/SectionBootstrapSessions";

const SECTION_COMPONENTS: Record<AccessSectionId, React.ComponentType> = {
  "admin-management": SectionAdminManagement,
  "bootstrap-sessions": SectionBootstrapSessions,
};

export default function AccessSectionPage() {
  const params = useParams();
  const section = params.section as string;
  const SectionContent = SECTION_COMPONENTS[section as AccessSectionId];
  if (!SectionContent) notFound();
  return <SectionContent />;
}
