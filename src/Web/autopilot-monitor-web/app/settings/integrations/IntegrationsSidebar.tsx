"use client";

import { usePageSections } from "../../../hooks/usePageSections";
import { PageSectionItem } from "../../../contexts/SidebarContext";
import { INTEGRATIONS_NAV_SECTIONS } from "./integrationsNavSections";
import { BellIcon, CloudArrowUpIcon } from "../../../lib/sidebarIcons";

const SECTION_ICONS: Record<string, React.ReactNode> = {
  "notifications": <BellIcon />,
  "diagnostics": <CloudArrowUpIcon />,
};

const integrationsItems: PageSectionItem[] = INTEGRATIONS_NAV_SECTIONS.map((s) => ({
  id: s.id,
  label: s.label,
  icon: SECTION_ICONS[s.id],
  href: `/settings/integrations/${s.id}`,
}));

export function IntegrationsSidebar({ children }: { children: React.ReactNode }) {
  usePageSections(integrationsItems, "Integrations", "route");

  return (
    <>
      <header className="bg-white shadow">
        <div className="py-6 px-4 sm:px-6 lg:px-8">
          <h1 className="text-2xl font-normal text-gray-900">Tenant Configuration</h1>
          <p className="text-sm text-gray-500 mt-1">Webhook notifications and diagnostics upload</p>
        </div>
      </header>
      <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-8">
        {children}
      </div>
    </>
  );
}
