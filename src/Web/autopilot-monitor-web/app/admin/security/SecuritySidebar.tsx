"use client";

import { usePageSections } from "../../../hooks/usePageSections";
import { PageSectionItem } from "../../../contexts/SidebarContext";
import { SECURITY_NAV_SECTIONS } from "./securityNavSections";
import { NoSymbolIcon, KeyIcon } from "../../../lib/sidebarIcons";

const SECTION_ICONS: Record<string, React.ReactNode> = {
  "device-block": <NoSymbolIcon />,
  "version-block": <NoSymbolIcon />,
  "vulnerability-data": <KeyIcon />,
};

const securityItems: PageSectionItem[] = SECURITY_NAV_SECTIONS.map((s) => ({
  id: s.id,
  label: s.label,
  icon: SECTION_ICONS[s.id],
  href: `/admin/security/${s.id}`,
}));

export function SecuritySidebar({ children }: { children: React.ReactNode }) {
  usePageSections(securityItems, "Security", "route");

  return (
    <>
      {/* Page header */}
      <header className="bg-white shadow">
        <div className="py-6 px-4 sm:px-6 lg:px-8">
          <h1 className="text-2xl font-normal text-gray-900">Security</h1>
          <p className="text-sm text-gray-500 mt-1">Device blocking and vulnerability management</p>
        </div>
      </header>

      {/* Content */}
      <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-8">
        {children}
      </div>
    </>
  );
}
