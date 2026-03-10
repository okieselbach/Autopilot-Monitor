"use client";

import { useEffect, useRef, useState, useCallback } from "react";

export interface SidebarSection {
  id: string;
  label: string;
}

interface ScrollSpySidebarProps {
  sections: SidebarSection[];
  title?: string;
  children: React.ReactNode;
}

export function ScrollSpySidebar({
  sections,
  title = "Contents",
  children,
}: ScrollSpySidebarProps) {
  const [activeId, setActiveId] = useState(sections[0]?.id ?? "");
  const [mobileSidebarOpen, setMobileSidebarOpen] = useState(false);
  const observerRef = useRef<IntersectionObserver | null>(null);
  // Track which sections are currently intersecting
  const visibleSections = useRef<Set<string>>(new Set());

  const activeLabel =
    sections.find((s) => s.id === activeId)?.label ?? title;

  // Set up IntersectionObserver for scroll-spy
  useEffect(() => {
    observerRef.current?.disconnect();

    const observer = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (entry.isIntersecting) {
            visibleSections.current.add(entry.target.id);
          } else {
            visibleSections.current.delete(entry.target.id);
          }
        }

        // Pick the first visible section in DOM order
        for (const s of sections) {
          if (visibleSections.current.has(s.id)) {
            setActiveId(s.id);
            break;
          }
        }
      },
      {
        rootMargin: "-80px 0px -60% 0px",
        threshold: 0,
      },
    );

    observerRef.current = observer;

    // Observe all section elements
    for (const s of sections) {
      const el = document.getElementById(s.id);
      if (el) observer.observe(el);
    }

    return () => observer.disconnect();
  }, [sections]);

  const scrollTo = useCallback(
    (id: string) => {
      const el = document.getElementById(id);
      if (el) {
        const y = el.getBoundingClientRect().top + window.scrollY - 90;
        window.scrollTo({ top: y, behavior: "smooth" });
      }
      setActiveId(id);
      setMobileSidebarOpen(false);
    },
    [],
  );

  const navItems = sections.map((s) => (
    <li key={s.id}>
      <button
        onClick={() => scrollTo(s.id)}
        className={`block w-full text-left px-3 py-2 rounded-md text-sm transition-colors ${
          activeId === s.id
            ? "bg-blue-50 text-blue-700 font-semibold"
            : "text-gray-600 hover:bg-gray-100 hover:text-gray-900"
        }`}
      >
        {s.label}
      </button>
    </li>
  ));

  return (
    <>
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
          <p className="text-xs font-semibold text-gray-400 uppercase tracking-wider">
            {title}
          </p>
          <button
            onClick={() => setMobileSidebarOpen(false)}
            className="p-1 rounded-md text-gray-400 hover:text-gray-600 hover:bg-gray-100"
          >
            <svg
              className="w-4 h-4"
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
              strokeWidth={2}
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                d="M6 18L18 6M6 6l12 12"
              />
            </svg>
          </button>
        </div>
        <ul className="p-3 space-y-0.5 overflow-y-auto max-h-[calc(100%-4rem)]">
          {navItems}
        </ul>
      </div>

      {/* Mobile: contents toggle bar */}
      <div className="md:hidden mb-4">
        <button
          onClick={() => setMobileSidebarOpen(true)}
          className="flex items-center gap-2 px-3 py-2 rounded-lg border border-gray-200 bg-white shadow-sm text-sm text-gray-600 hover:bg-gray-50 transition-colors"
        >
          <svg
            className="w-4 h-4 text-gray-400"
            fill="none"
            viewBox="0 0 24 24"
            stroke="currentColor"
            strokeWidth={2}
          >
            <path
              strokeLinecap="round"
              strokeLinejoin="round"
              d="M4 6h16M4 12h16M4 18h7"
            />
          </svg>
          <span className="font-medium text-gray-700">{activeLabel}</span>
          <svg
            className="w-3.5 h-3.5 text-gray-400 ml-auto"
            fill="none"
            viewBox="0 0 24 24"
            stroke="currentColor"
            strokeWidth={2}
          >
            <path
              strokeLinecap="round"
              strokeLinejoin="round"
              d="M9 5l7 7-7 7"
            />
          </svg>
        </button>
      </div>

      {/* Two-column layout */}
      <div className="flex gap-8 items-start">
        {/* Desktop sidebar */}
        <aside className="w-52 shrink-0 hidden md:block">
          <nav className="sticky top-24 bg-white rounded-lg shadow-sm border border-gray-200 p-4">
            <p className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-3 px-1">
              {title}
            </p>
            <ul className="space-y-0.5">{navItems}</ul>
          </nav>
        </aside>

        {/* Content */}
        <div className="flex-1 min-w-0">{children}</div>
      </div>
    </>
  );
}
