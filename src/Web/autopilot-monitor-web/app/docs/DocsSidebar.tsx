"use client";

import { usePageSections } from "../../hooks/usePageSections";
import { PageSectionItem } from "../../contexts/SidebarContext";
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
  CodeBracketIcon,
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
  "ime-log-patterns": <CodeBracketIcon />,
};

const docsItems: PageSectionItem[] = NAV_SECTIONS.map((s) => ({
  id: s.id,
  label: s.label,
  icon: SECTION_ICONS[s.id],
  href: `/docs/${s.id}`,
}));

export function DocsSidebar({ children }: { children: React.ReactNode }) {
  usePageSections(docsItems, "Contents", "route");

  return (
    <>
      {/* Page header */}
      <header className="bg-white shadow">
        <div className="py-6 px-4 sm:px-6 lg:px-8">
          <h1 className="text-3xl font-bold text-gray-900">Documentation</h1>
        </div>
      </header>

      {/* Content */}
      <div className="px-4 sm:px-6 lg:px-8 py-8 space-y-8">
        {children}
      </div>
    </>
  );
}
