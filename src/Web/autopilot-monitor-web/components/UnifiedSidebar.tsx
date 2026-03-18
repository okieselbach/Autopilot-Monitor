"use client";

import { useEffect, useRef, useState, useCallback } from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useSidebarState, CollapseState } from "../hooks/useSidebarState";
import { DefaultSectionIcon } from "../lib/sidebarIcons";

export interface SidebarItem {
  id: string;
  label: string;
  icon?: React.ReactNode;
  href?: string;
}

interface UnifiedSidebarProps {
  items: SidebarItem[];
  mode: "scroll-spy" | "route";
  title?: string;
  children: React.ReactNode;
}

export function UnifiedSidebar({
  items,
  mode,
  title = "Contents",
  children,
}: UnifiedSidebarProps) {
  const { collapseState, cycleCollapseState, setCollapseState } = useSidebarState();
  const [activeId, setActiveId] = useState(items[0]?.id ?? "");
  const [mobileDrawerOpen, setMobileDrawerOpen] = useState(false);
  const observerRef = useRef<IntersectionObserver | null>(null);
  const visibleSections = useRef<Set<string>>(new Set());
  const pathname = usePathname();

  // --- Scroll-spy mode: IntersectionObserver ---
  useEffect(() => {
    if (mode !== "scroll-spy") return;

    observerRef.current?.disconnect();
    visibleSections.current.clear();

    const observer = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (entry.isIntersecting) {
            visibleSections.current.add(entry.target.id);
          } else {
            visibleSections.current.delete(entry.target.id);
          }
        }
        // Pick first visible section in DOM order
        for (const item of items) {
          if (visibleSections.current.has(item.id)) {
            setActiveId(item.id);
            break;
          }
        }
      },
      { rootMargin: "-80px 0px -60% 0px", threshold: 0 },
    );

    observerRef.current = observer;

    for (const item of items) {
      const el = document.getElementById(item.id);
      if (el) observer.observe(el);
    }

    return () => observer.disconnect();
  }, [items, mode]);

  // --- Route mode: track active from pathname ---
  useEffect(() => {
    if (mode !== "route") return;
    const segment = pathname.split("/").pop() ?? "";
    const match = items.find((item) => item.id === segment);
    if (match) setActiveId(match.id);
  }, [pathname, items, mode]);

  const scrollTo = useCallback((id: string) => {
    const el = document.getElementById(id);
    if (el) {
      const y = el.getBoundingClientRect().top + window.scrollY - 90;
      window.scrollTo({ top: y, behavior: "smooth" });
    }
    setActiveId(id);
    setMobileDrawerOpen(false);
  }, []);

  const handleItemClick = useCallback(
    (item: SidebarItem) => {
      if (mode === "scroll-spy") {
        scrollTo(item.id);
      } else {
        setMobileDrawerOpen(false);
      }
    },
    [mode, scrollTo],
  );

  const activeLabel = items.find((i) => i.id === activeId)?.label ?? title;

  // Width classes for each collapse state
  const sidebarWidthClass: Record<CollapseState, string> = {
    full: "w-52",
    icons: "w-14",
    hidden: "w-0",
  };

  const renderIcon = (item: SidebarItem, sizeClass = "w-5 h-5") => {
    if (item.icon) {
      return <span className={`shrink-0 ${sizeClass}`}>{item.icon}</span>;
    }
    return <DefaultSectionIcon className={`shrink-0 ${sizeClass}`} />;
  };

  const renderNavItem = (item: SidebarItem) => {
    const isActive = activeId === item.id;
    const baseClass = `flex items-center gap-2.5 rounded-md text-sm transition-colors ${
      isActive
        ? "bg-blue-50 text-blue-700 font-semibold dark:bg-blue-900/30 dark:text-blue-300"
        : "text-gray-600 hover:bg-gray-100 hover:text-gray-900 dark:text-gray-400 dark:hover:bg-gray-700 dark:hover:text-gray-200"
    }`;

    // Icons-only mode: centered icon with tooltip
    if (collapseState === "icons") {
      const content = (
        <span className="flex items-center justify-center w-full">
          {renderIcon(item, "w-4.5 h-4.5")}
        </span>
      );

      if (mode === "route" && item.href) {
        return (
          <li key={item.id}>
            <Link
              href={item.href}
              onClick={() => handleItemClick(item)}
              className={`${baseClass} px-2 py-2 justify-center relative group`}
              title={item.label}
            >
              {content}
              <span className="absolute left-full ml-2 px-2 py-1 rounded bg-gray-900 text-white text-xs whitespace-nowrap opacity-0 pointer-events-none group-hover:opacity-100 transition-opacity z-50 dark:bg-gray-700">
                {item.label}
              </span>
            </Link>
          </li>
        );
      }

      return (
        <li key={item.id}>
          <button
            onClick={() => handleItemClick(item)}
            className={`${baseClass} w-full px-2 py-2 justify-center relative group`}
            title={item.label}
          >
            {content}
            <span className="absolute left-full ml-2 px-2 py-1 rounded bg-gray-900 text-white text-xs whitespace-nowrap opacity-0 pointer-events-none group-hover:opacity-100 transition-opacity z-50 dark:bg-gray-700">
              {item.label}
            </span>
          </button>
        </li>
      );
    }

    // Full mode: icon + label
    if (mode === "route" && item.href) {
      return (
        <li key={item.id}>
          <Link
            href={item.href}
            onClick={() => handleItemClick(item)}
            className={`${baseClass} px-3 py-2`}
          >
            {renderIcon(item, "w-4 h-4")}
            <span className="truncate">{item.label}</span>
          </Link>
        </li>
      );
    }

    return (
      <li key={item.id}>
        <button
          onClick={() => handleItemClick(item)}
          className={`${baseClass} w-full text-left px-3 py-2`}
        >
          {renderIcon(item, "w-4 h-4")}
          <span className="truncate">{item.label}</span>
        </button>
      </li>
    );
  };

  // Shared nav items for both desktop and mobile
  const navItems = items.map(renderNavItem);

  return (
    <>
      {/* ===== Mobile: overlay ===== */}
      {mobileDrawerOpen && (
        <div
          className="fixed inset-0 z-40 bg-black/40 md:hidden"
          onClick={() => setMobileDrawerOpen(false)}
        />
      )}

      {/* ===== Mobile: drawer ===== */}
      <div
        className={`fixed top-0 left-0 z-50 h-full w-56 bg-white shadow-xl transition-transform duration-200 md:hidden dark:bg-gray-800 ${
          mobileDrawerOpen ? "translate-x-0" : "-translate-x-full"
        }`}
      >
        <div className="flex items-center justify-between px-4 pt-5 pb-3 border-b border-gray-100 dark:border-gray-700">
          <p className="text-xs font-semibold text-gray-400 uppercase tracking-wider dark:text-gray-500">
            {title}
          </p>
          <button
            onClick={() => setMobileDrawerOpen(false)}
            className="p-1 rounded-md text-gray-400 hover:text-gray-600 hover:bg-gray-100 dark:hover:text-gray-300 dark:hover:bg-gray-700"
          >
            <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        </div>
        <ul className="p-3 space-y-0.5 overflow-y-auto max-h-[calc(100%-4rem)]">
          {/* In mobile drawer, always show full items (icon + label) */}
          {items.map((item) => {
            const isActive = activeId === item.id;
            const cls = `flex items-center gap-2.5 w-full text-left px-3 py-2 rounded-md text-sm transition-colors ${
              isActive
                ? "bg-blue-50 text-blue-700 font-semibold dark:bg-blue-900/30 dark:text-blue-300"
                : "text-gray-600 hover:bg-gray-100 hover:text-gray-900 dark:text-gray-400 dark:hover:bg-gray-700 dark:hover:text-gray-200"
            }`;

            if (mode === "route" && item.href) {
              return (
                <li key={item.id}>
                  <Link href={item.href} onClick={() => setMobileDrawerOpen(false)} className={cls}>
                    {renderIcon(item, "w-4 h-4")}
                    <span className="truncate">{item.label}</span>
                  </Link>
                </li>
              );
            }

            return (
              <li key={item.id}>
                <button onClick={() => scrollTo(item.id)} className={cls}>
                  {renderIcon(item, "w-4 h-4")}
                  <span className="truncate">{item.label}</span>
                </button>
              </li>
            );
          })}
        </ul>
      </div>

      {/* ===== Mobile: toggle bar ===== */}
      <div className="md:hidden mb-4">
        <button
          onClick={() => setMobileDrawerOpen(true)}
          className="flex items-center gap-2 px-3 py-2 rounded-lg border border-gray-200 bg-white shadow-sm text-sm text-gray-600 hover:bg-gray-50 transition-colors dark:border-gray-700 dark:bg-gray-800 dark:text-gray-400 dark:hover:bg-gray-700"
        >
          <svg className="w-4 h-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M4 6h16M4 12h16M4 18h7" />
          </svg>
          <span className="font-medium text-gray-700 dark:text-gray-300">{activeLabel}</span>
          <svg className="w-3.5 h-3.5 text-gray-400 ml-auto" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
          </svg>
        </button>
      </div>

      {/* ===== Desktop: two-column flex layout ===== */}
      <div className="flex gap-0 items-start">
        {/* Desktop sidebar */}
        <aside
          className={`shrink-0 hidden md:block transition-all duration-200 ease-in-out overflow-hidden ${sidebarWidthClass[collapseState]}`}
        >
          {collapseState !== "hidden" && (
            <nav className="sticky top-24 bg-white rounded-lg shadow-sm border border-gray-200 dark:bg-gray-800 dark:border-gray-700">
              {/* Title (full mode only) */}
              {collapseState === "full" && (
                <p className="text-xs font-semibold text-gray-400 uppercase tracking-wider px-4 pt-4 pb-1 dark:text-gray-500">
                  {title}
                </p>
              )}

              {/* Nav items */}
              <ul className={`space-y-0.5 overflow-y-auto max-h-[calc(100vh-8rem)] ${
                collapseState === "full" ? "p-3 pt-2" : "p-1.5"
              }`}>
                {navItems}
              </ul>
            </nav>
          )}
        </aside>

        {/* Edge-chevron toggle (desktop only) */}
        <div className="hidden md:flex items-start shrink-0">
          <button
            onClick={collapseState === "hidden" ? () => setCollapseState("full") : cycleCollapseState}
            className="sticky top-24 mt-1 flex h-8 w-4 items-center justify-center rounded-r-md bg-gray-200/60 text-gray-400 transition-colors hover:bg-gray-300 hover:text-gray-600 dark:bg-gray-700/60 dark:text-gray-500 dark:hover:bg-gray-600 dark:hover:text-gray-300"
            aria-label={collapseState === "hidden" ? "Show sidebar" : "Collapse sidebar"}
            title={
              collapseState === "full"
                ? "Show icons only"
                : collapseState === "icons"
                  ? "Hide sidebar"
                  : "Show sidebar"
            }
          >
            <svg
              className={`w-3 h-3 transition-transform duration-200 ${collapseState === "hidden" ? "" : "rotate-180"}`}
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
              strokeWidth={2}
            >
              <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
            </svg>
          </button>
        </div>

        {/* Content */}
        <div className={`flex-1 min-w-0 ${collapseState !== "hidden" ? "md:ml-2" : ""}`}>{children}</div>
      </div>
    </>
  );
}
