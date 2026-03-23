"use client";

import { usePageSections } from "../../../hooks/usePageSections";
import { PageSectionItem } from "../../../contexts/SidebarContext";
import { AGENT_NAV_SECTIONS } from "./agentNavSections";
import { GearIcon, MagnifyingGlassIcon, LockClosedIcon } from "../../../lib/sidebarIcons";
import { useTenantConfig } from "../TenantConfigContext";

const SECTION_ICONS: Record<string, React.ReactNode> = {
  "settings": <GearIcon />,
  "analyzers": <MagnifyingGlassIcon />,
  "unrestricted-mode": <LockClosedIcon />,
};

export function AgentSidebar({ children }: { children: React.ReactNode }) {
  const { config } = useTenantConfig();

  // Filter out unrestricted-mode if not enabled
  const visibleSections = AGENT_NAV_SECTIONS.filter(
    (s) => s.id !== "unrestricted-mode" || config?.unrestrictedModeEnabled
  );

  const agentItems: PageSectionItem[] = visibleSections.map((s) => ({
    id: s.id,
    label: s.label,
    icon: SECTION_ICONS[s.id],
    href: `/settings/agent/${s.id}`,
  }));

  usePageSections(agentItems, "Agent", "route");

  return (
    <>
      <header className="bg-white shadow">
        <div className="py-6 px-4 sm:px-6 lg:px-8">
          <h1 className="text-2xl font-normal text-gray-900">Tenant Configuration</h1>
          <p className="text-sm text-gray-500 mt-1">Agent collector and behavior configuration</p>
        </div>
      </header>
      <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-8">
        {children}
      </div>
    </>
  );
}
