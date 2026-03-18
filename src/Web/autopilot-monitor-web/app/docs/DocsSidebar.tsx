"use client";

import { useRouter } from "next/navigation";
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

const docsItems: PageSectionItem[] = NAV_SECTIONS.map((s) => ({
  id: s.id,
  label: s.label,
  icon: SECTION_ICONS[s.id],
  href: `/docs/${s.id}`,
}));

export function DocsSidebar({ children }: { children: React.ReactNode }) {
  const router = useRouter();

  usePageSections(docsItems, "Contents", "route");

  return (
    <>
      {/* Page header */}
      <header className="bg-white shadow-sm border-b border-gray-200 sticky top-14 z-10">
        <div className="px-4 sm:px-6 lg:px-8 py-4">
          <div className="flex items-center justify-between">
            <button
              onClick={() => router.back()}
              className="flex items-center space-x-2 text-gray-600 hover:text-gray-900 transition-colors"
            >
              <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10 19l-7-7m0 0l7-7m-7 7h18" />
              </svg>
              <span>Back</span>
            </button>
            <h1 className="text-2xl font-bold text-blue-600">Documentation</h1>
          </div>
        </div>
      </header>

      {/* Content */}
      <div className="px-4 sm:px-6 lg:px-8 py-8 space-y-8">
        {children}
        <div className="text-center text-sm text-gray-500 pb-4">
          <p>Autopilot Monitor v1.0.0</p>
        </div>
      </div>
    </>
  );
}
