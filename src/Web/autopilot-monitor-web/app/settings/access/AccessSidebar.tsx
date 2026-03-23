"use client";

import { usePageSections } from "../../../hooks/usePageSections";
import { PageSectionItem } from "../../../contexts/SidebarContext";
import { ACCESS_NAV_SECTIONS } from "./accessNavSections";
import { UsersIcon, KeyIcon } from "../../../lib/sidebarIcons";
import { useTenantConfig } from "../TenantConfigContext";

const SECTION_ICONS: Record<string, React.ReactNode> = {
  "admin-management": <UsersIcon />,
  "bootstrap-sessions": <KeyIcon />,
};

export function AccessSidebar({ children }: { children: React.ReactNode }) {
  const { config, user } = useTenantConfig();

  // Filter: hide bootstrap-sessions if not enabled; show admin-management only for admins
  const visibleSections = ACCESS_NAV_SECTIONS.filter((s) => {
    if (s.id === "bootstrap-sessions" && !config?.bootstrapTokenEnabled) return false;
    if (s.id === "admin-management" && !user?.isTenantAdmin && !user?.isGalacticAdmin) return false;
    return true;
  });

  const accessItems: PageSectionItem[] = visibleSections.map((s) => ({
    id: s.id,
    label: s.label,
    icon: SECTION_ICONS[s.id],
    href: `/settings/access/${s.id}`,
  }));

  usePageSections(accessItems, "Access", "route");

  return (
    <>
      <header className="bg-white shadow">
        <div className="py-6 px-4 sm:px-6 lg:px-8">
          <h1 className="text-2xl font-normal text-gray-900">Tenant Configuration</h1>
          <p className="text-sm text-gray-500 mt-1">Admin management and bootstrap sessions</p>
        </div>
      </header>
      <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-8">
        {children}
      </div>
    </>
  );
}
