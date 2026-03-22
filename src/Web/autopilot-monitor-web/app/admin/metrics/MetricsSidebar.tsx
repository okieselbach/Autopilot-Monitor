"use client";

import { usePageSections } from "../../../hooks/usePageSections";
import { PageSectionItem } from "../../../contexts/SidebarContext";
import { METRICS_NAV_SECTIONS } from "./metricsNavSections";
import { ChartBarIcon } from "../../../lib/sidebarIcons";

function TrendingUpIcon({ className = "w-5 h-5" }: { className?: string }) {
  return (
    <svg className={className} fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M2.25 18L9 11.25l4.306 4.307a11.95 11.95 0 015.814-5.519l2.74-1.22m0 0l-5.94-2.28m5.94 2.28l-2.28 5.941" />
    </svg>
  );
}

const SECTION_ICONS: Record<string, React.ReactNode> = {
  "platform-metrics": <ChartBarIcon />,
  "platform-usage": <TrendingUpIcon />,
};

const metricsItems: PageSectionItem[] = METRICS_NAV_SECTIONS.map((s) => ({
  id: s.id,
  label: s.label,
  icon: SECTION_ICONS[s.id],
  href: `/admin/metrics/${s.id}`,
}));

export function MetricsSidebar({ children }: { children: React.ReactNode }) {
  usePageSections(metricsItems, "Metrics", "route");

  return (
    <>
      {/* Page header */}
      <header className="bg-white shadow">
        <div className="py-6 px-4 sm:px-6 lg:px-8">
          <h1 className="text-2xl font-normal text-gray-900">Metrics</h1>
          <p className="text-sm text-gray-500 mt-1">Platform-wide analytics</p>
        </div>
      </header>

      {/* Content */}
      <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-8">
        {children}
      </div>
    </>
  );
}
