"use client";

import { UnifiedSidebar, SidebarItem } from "../../components/UnifiedSidebar";
import { NAV_SECTIONS } from "./docsNavSections";
import {
  KeyIcon,
  BookOpenIcon,
  InformationCircleIcon,
  WrenchScrewdriverIcon,
  ComputerDesktopIcon,
  RocketLaunchIcon,
  GearIcon,
  ListBulletIcon,
  SparklesIcon,
} from "../../lib/sidebarIcons";

const SECTION_ICONS: Record<string, React.ReactNode> = {
  "private-preview": <KeyIcon />,
  "overview": <BookOpenIcon />,
  "general": <InformationCircleIcon />,
  "setup": <WrenchScrewdriverIcon />,
  "agent": <ComputerDesktopIcon />,
  "agent-setup": <RocketLaunchIcon />,
  "settings": <GearIcon />,
  "gather-rules": <ListBulletIcon />,
  "analyze-rules": <SparklesIcon />,
};

const docsItems: SidebarItem[] = NAV_SECTIONS.map((s) => ({
  id: s.id,
  label: s.label,
  icon: SECTION_ICONS[s.id],
  href: `/docs/${s.id}`,
}));

export function DocsSidebar({ children }: { children: React.ReactNode }) {
  return (
    <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
      <UnifiedSidebar items={docsItems} mode="route" title="Contents">
        <div className="space-y-8">
          {children}
          <div className="text-center text-sm text-gray-500 pb-4">
            <p>Autopilot Monitor v1.0.0</p>
          </div>
        </div>
      </UnifiedSidebar>
    </div>
  );
}
