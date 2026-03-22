"use client";

import { useParams } from "next/navigation";
import { notFound } from "next/navigation";
import { REPORTS_NAV_SECTIONS, type ReportsSectionId } from "../reportsNavSections";
import { SectionSessionReports } from "../sections/SectionSessionReports";
import { SectionUserFeedback } from "../sections/SectionUserFeedback";
import { SectionSessionExport } from "../sections/SectionSessionExport";

const SECTION_COMPONENTS: Record<ReportsSectionId, React.ComponentType> = {
  "session-reports": SectionSessionReports,
  "user-feedback": SectionUserFeedback,
  "session-export": SectionSessionExport,
};

export default function ReportsSectionPage() {
  const params = useParams();
  const section = params.section as string;
  const SectionContent = SECTION_COMPONENTS[section as ReportsSectionId];
  if (!SectionContent) notFound();
  return <SectionContent />;
}
