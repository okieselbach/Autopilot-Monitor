"use client";

import { usePageSections } from "../../../hooks/usePageSections";
import { PageSectionItem } from "../../../contexts/SidebarContext";
import { SETTINGS_NAV_SECTIONS } from "./settingsNavSections";
import { GearIcon, FolderIcon, ArrowPathIcon } from "../../../lib/sidebarIcons";

const SECTION_ICONS: Record<string, React.ReactNode> = {
  "global": <GearIcon />,
  "diagnostics-log-paths": <FolderIcon />,
  "config-reseed": <ArrowPathIcon />,
};

const settingsItems: PageSectionItem[] = SETTINGS_NAV_SECTIONS.map((s) => ({
  id: s.id,
  label: s.label,
  icon: SECTION_ICONS[s.id],
  href: `/admin/settings/${s.id}`,
}));

export function SettingsSidebar({ children }: { children: React.ReactNode }) {
  usePageSections(settingsItems, "Settings", "route");

  return (
    <>
      {/* Page header */}
      <header className="bg-white shadow">
        <div className="py-6 px-4 sm:px-6 lg:px-8">
          <h1 className="text-2xl font-normal text-gray-900">Settings</h1>
          <p className="text-sm text-gray-500 mt-1">Global platform configuration</p>
        </div>
      </header>

      {/* Content */}
      <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-8">
        {children}
      </div>
    </>
  );
}
