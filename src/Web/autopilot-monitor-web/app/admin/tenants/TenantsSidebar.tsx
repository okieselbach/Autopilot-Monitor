"use client";

import { usePageSections } from "../../../hooks/usePageSections";
import { PageSectionItem } from "../../../contexts/SidebarContext";
import { TENANTS_NAV_SECTIONS } from "./tenantsNavSections";
import {
  BuildingOfficeIcon,
  DocumentTextIcon,
} from "../../../lib/sidebarIcons";

const SECTION_ICONS: Record<string, React.ReactNode> = {
  "management": <BuildingOfficeIcon />,
  "config-report": <DocumentTextIcon />,
};

const tenantsItems: PageSectionItem[] = TENANTS_NAV_SECTIONS.map((s) => ({
  id: s.id,
  label: s.label,
  icon: SECTION_ICONS[s.id],
  href: `/admin/tenants/${s.id}`,
}));

export function TenantsSidebar({ children }: { children: React.ReactNode }) {
  usePageSections(tenantsItems, "Tenants", "route");

  return (
    <>
      {/* Page header */}
      <header className="bg-white shadow">
        <div className="py-6 px-4 sm:px-6 lg:px-8">
          <h1 className="text-2xl font-normal text-gray-900">Tenants</h1>
          <p className="mt-1 text-sm text-gray-500">Tenant management and configuration</p>
        </div>
      </header>

      {/* Content */}
      <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-8">
        {children}
      </div>
    </>
  );
}
