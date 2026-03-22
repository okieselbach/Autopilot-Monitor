"use client";

import { usePageSections } from "../../../hooks/usePageSections";
import { PageSectionItem } from "../../../contexts/SidebarContext";
import { REPORTS_NAV_SECTIONS } from "./reportsNavSections";
import { DocumentTextIcon, SparklesIcon, ArrowDownTrayIcon } from "../../../lib/sidebarIcons";

const SECTION_ICONS: Record<string, React.ReactNode> = {
  "session-reports": <DocumentTextIcon />,
  "user-feedback": <SparklesIcon />,
  "session-export": <ArrowDownTrayIcon />,
};

const reportsItems: PageSectionItem[] = REPORTS_NAV_SECTIONS.map((s) => ({
  id: s.id,
  label: s.label,
  icon: SECTION_ICONS[s.id],
  href: `/admin/reports/${s.id}`,
}));

export function ReportsSidebar({ children }: { children: React.ReactNode }) {
  usePageSections(reportsItems, "Reports", "route");

  return (
    <>
      {/* Page header */}
      <header className="bg-white shadow">
        <div className="py-6 px-4 sm:px-6 lg:px-8">
          <h1 className="text-2xl font-normal text-gray-900">Reports</h1>
          <p className="text-sm text-gray-500 mt-1">Session reports and user feedback</p>
        </div>
      </header>

      {/* Content */}
      <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-8">
        {children}
      </div>
    </>
  );
}
