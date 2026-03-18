"use client";

import { useEffect, useRef } from "react";
import { useSidebar, PageSectionItem } from "../contexts/SidebarContext";

/**
 * Register page-specific sections in the global sidebar.
 * Sections appear below the global nav with a divider.
 * Automatically clears on unmount.
 *
 * Uses a stable comparison (by id list) to avoid re-renders
 * when items array reference changes but content is the same.
 */
export function usePageSections(
  items: PageSectionItem[],
  title: string,
  mode: "scroll-spy" | "route" = "scroll-spy",
) {
  const { setPageSections, clearPageSections } = useSidebar();
  const prevKeyRef = useRef("");

  useEffect(() => {
    // Build a stable key from item ids to detect actual changes
    const key = items.map((i) => i.id).join("|") + "|" + title + "|" + mode;

    if (key !== prevKeyRef.current && items.length > 0) {
      prevKeyRef.current = key;
      setPageSections(items, title, mode);
    }

    return () => {
      prevKeyRef.current = "";
      clearPageSections();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [items.length, title, mode]);
}
