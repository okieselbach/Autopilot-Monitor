"use client";

import { useState, useEffect, useRef, useCallback } from "react";

export interface SectionEntry {
  id: string;
  label: string;
}

interface SectionNavProps {
  sections: SectionEntry[];
}

export default function SectionNav({ sections }: SectionNavProps) {
  const [open, setOpen] = useState(false);
  const [activeId, setActiveId] = useState<string | null>(null);
  const panelRef = useRef<HTMLDivElement>(null);

  // Track active section via IntersectionObserver
  useEffect(() => {
    if (sections.length === 0) return;

    const observers: IntersectionObserver[] = [];
    const visibleSections = new Map<string, number>();

    sections.forEach((section) => {
      const el = document.getElementById(section.id);
      if (!el) return;

      const observer = new IntersectionObserver(
        (entries) => {
          entries.forEach((entry) => {
            if (entry.isIntersecting) {
              visibleSections.set(section.id, entry.intersectionRatio);
            } else {
              visibleSections.delete(section.id);
            }

            // Pick topmost visible section
            let topId: string | null = null;
            let topY = Infinity;
            visibleSections.forEach((_, id) => {
              const rect = document.getElementById(id)?.getBoundingClientRect();
              if (rect && rect.top < topY) {
                topY = rect.top;
                topId = id;
              }
            });
            if (topId) setActiveId(topId);
          });
        },
        { rootMargin: "-10% 0px -60% 0px", threshold: 0 }
      );

      observer.observe(el);
      observers.push(observer);
    });

    return () => observers.forEach((o) => o.disconnect());
  }, [sections]);

  // Close on click outside
  useEffect(() => {
    if (!open) return;
    const handler = (e: MouseEvent) => {
      if (panelRef.current && !panelRef.current.contains(e.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, [open]);

  const scrollTo = useCallback(
    (id: string) => {
      document.getElementById(id)?.scrollIntoView({ behavior: "smooth" });
      setOpen(false);
    },
    []
  );

  if (sections.length === 0) return null;

  return (
    <div
      ref={panelRef}
      className="fixed left-0 top-1/2 z-40 -translate-y-1/2"
    >
      {/* Collapsed tab */}
      {!open && (
        <button
          onClick={() => setOpen(true)}
          className="flex h-16 w-4 items-center justify-center rounded-r-md bg-gray-300/60 text-gray-500 transition-colors hover:bg-gray-300 hover:text-gray-700 dark:bg-gray-700/60 dark:text-gray-400 dark:hover:bg-gray-700 dark:hover:text-gray-200"
          aria-label="Open section navigation"
        >
          <svg
            xmlns="http://www.w3.org/2000/svg"
            className="h-3 w-3"
            viewBox="0 0 20 20"
            fill="currentColor"
          >
            <path
              fillRule="evenodd"
              d="M7.293 14.707a1 1 0 010-1.414L10.586 10 7.293 6.707a1 1 0 011.414-1.414l4 4a1 1 0 010 1.414l-4 4a1 1 0 01-1.414 0z"
              clipRule="evenodd"
            />
          </svg>
        </button>
      )}

      {/* Expanded panel */}
      {open && (
        <div className="flex animate-slide-in-left flex-col rounded-r-lg bg-white/95 py-3 pl-3 pr-4 shadow-lg backdrop-blur dark:bg-gray-800/95">
          <div className="mb-2 flex items-center justify-between">
            <span className="text-xs font-semibold uppercase tracking-wider text-gray-400 dark:text-gray-500">
              Sections
            </span>
            <button
              onClick={() => setOpen(false)}
              className="ml-4 text-gray-400 hover:text-gray-600 dark:hover:text-gray-300"
              aria-label="Close section navigation"
            >
              <svg
                xmlns="http://www.w3.org/2000/svg"
                className="h-3 w-3"
                viewBox="0 0 20 20"
                fill="currentColor"
              >
                <path
                  fillRule="evenodd"
                  d="M12.707 5.293a1 1 0 010 1.414L9.414 10l3.293 3.293a1 1 0 01-1.414 1.414l-4-4a1 1 0 010-1.414l4-4a1 1 0 011.414 0z"
                  clipRule="evenodd"
                />
              </svg>
            </button>
          </div>
          <nav className="flex flex-col gap-0.5">
            {sections.map((s) => (
              <button
                key={s.id}
                onClick={() => scrollTo(s.id)}
                className={`whitespace-nowrap rounded px-2 py-1.5 text-left text-sm transition-colors ${
                  activeId === s.id
                    ? "bg-blue-50 font-medium text-blue-700 dark:bg-blue-900/30 dark:text-blue-300"
                    : "text-gray-600 hover:bg-gray-100 hover:text-gray-900 dark:text-gray-400 dark:hover:bg-gray-700 dark:hover:text-gray-200"
                }`}
              >
                {s.label}
              </button>
            ))}
          </nav>
        </div>
      )}
    </div>
  );
}
