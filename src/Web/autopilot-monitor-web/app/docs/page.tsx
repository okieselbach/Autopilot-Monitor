"use client";

import { useEffect, useState } from "react";
import { PublicSiteNavbar } from "../../components/PublicSiteNavbar";
import { PublicPageHeader } from "../../components/PublicPageHeader";
import { SectionPrivatePreview } from "./sections/SectionPrivatePreview";
import { SectionOverview } from "./sections/SectionOverview";
import { SectionSetup } from "./sections/SectionSetup";
import { SectionAgentSetup } from "./sections/SectionAgentSetup";
import { SectionSettings } from "./sections/SectionSettings";
import { SectionGatherRules } from "./sections/SectionGatherRules";
import { SectionAnalyzeRules } from "./sections/SectionAnalyzeRules";

const NAV_SECTIONS = [
  { id: "private-preview", label: "Private Preview" },
  { id: "overview",        label: "Overview" },
  { id: "setup",           label: "Setup" },
  { id: "agent-setup",     label: "Agent Setup" },
  { id: "settings",        label: "Settings" },
  { id: "gather-rules",    label: "Gather Rules" },
  { id: "analyze-rules",   label: "Analyze Rules" },
];

const SECTION_COMPONENTS: Record<string, () => JSX.Element> = {
  "private-preview":  SectionPrivatePreview,
  "overview":         SectionOverview,
  "setup":            SectionSetup,
  "agent-setup":      SectionAgentSetup,
  "settings":         SectionSettings,
  "gather-rules":     SectionGatherRules,
  "analyze-rules":    SectionAnalyzeRules,
};

export default function DocsPage() {
  const [activeSection, setActiveSection] = useState("overview");
  const [mobileSidebarOpen, setMobileSidebarOpen] = useState(false);

  useEffect(() => {
    const hash = window.location.hash.replace("#", "");
    if (hash && NAV_SECTIONS.some((s) => s.id === hash)) {
      setActiveSection(hash);
    }
  }, []);

  const ActiveContent = SECTION_COMPONENTS[activeSection] ?? SectionOverview;

  const activeLabel = NAV_SECTIONS.find((s) => s.id === activeSection)?.label ?? "Contents";

  return (
    <div className="min-h-screen bg-gray-50">
      <PublicSiteNavbar showSectionLinks={false} />
      <PublicPageHeader title="Documentation" />

      {/* Mobile sidebar overlay */}
      {mobileSidebarOpen && (
        <div
          className="fixed inset-0 z-40 bg-black/40 md:hidden"
          onClick={() => setMobileSidebarOpen(false)}
        />
      )}

      {/* Mobile sidebar drawer */}
      <div
        className={`fixed top-0 left-0 z-50 h-full w-56 bg-white shadow-xl transition-transform duration-200 md:hidden ${
          mobileSidebarOpen ? "translate-x-0" : "-translate-x-full"
        }`}
      >
        <div className="flex items-center justify-between px-4 pt-5 pb-3 border-b border-gray-100">
          <p className="text-xs font-semibold text-gray-400 uppercase tracking-wider">Contents</p>
          <button
            onClick={() => setMobileSidebarOpen(false)}
            className="p-1 rounded-md text-gray-400 hover:text-gray-600 hover:bg-gray-100"
          >
            <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        </div>
        <ul className="p-3 space-y-0.5">
          {NAV_SECTIONS.map((s) => (
            <li key={s.id}>
              <button
                onClick={() => { setActiveSection(s.id); setMobileSidebarOpen(false); }}
                className={`w-full text-left px-3 py-2 rounded-md text-sm transition-colors ${
                  activeSection === s.id
                    ? "bg-blue-50 text-blue-700 font-semibold"
                    : "text-gray-600 hover:bg-gray-100 hover:text-gray-900"
                }`}
              >
                {s.label}
              </button>
            </li>
          ))}
        </ul>
      </div>

      {/* Two-column layout */}
      <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 flex gap-8 items-start">

        {/* Desktop sidebar */}
        <aside className="w-52 shrink-0 hidden md:block">
          <nav className="sticky top-24 bg-white rounded-lg shadow-sm border border-gray-200 p-4">
            <p className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-3 px-1">
              Contents
            </p>
            <ul className="space-y-0.5">
              {NAV_SECTIONS.map((s) => (
                <li key={s.id}>
                  <button
                    onClick={() => setActiveSection(s.id)}
                    className={`w-full text-left px-3 py-2 rounded-md text-sm transition-colors ${
                      activeSection === s.id
                        ? "bg-blue-50 text-blue-700 font-semibold"
                        : "text-gray-600 hover:bg-gray-100 hover:text-gray-900"
                    }`}
                  >
                    {s.label}
                  </button>
                </li>
              ))}
            </ul>
          </nav>
        </aside>

        {/* Content — only the active section is rendered */}
        <main className="flex-1 min-w-0 space-y-8">

          {/* Mobile: contents toggle bar */}
          <div className="md:hidden">
            <button
              onClick={() => setMobileSidebarOpen(true)}
              className="flex items-center gap-2 px-3 py-2 rounded-lg border border-gray-200 bg-white shadow-sm text-sm text-gray-600 hover:bg-gray-50 transition-colors"
            >
              <svg className="w-4 h-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M4 6h16M4 12h16M4 18h7" />
              </svg>
              <span className="font-medium text-gray-700">{activeLabel}</span>
              <svg className="w-3.5 h-3.5 text-gray-400 ml-auto" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
              </svg>
            </button>
          </div>

          <ActiveContent />
          <div className="text-center text-sm text-gray-500 pb-4">
            <p>Autopilot Monitor v1.0.0</p>
            <p className="mt-1">Documentation last updated: {new Date().toLocaleDateString()}</p>
          </div>
        </main>

      </div>
    </div>
  );
}
