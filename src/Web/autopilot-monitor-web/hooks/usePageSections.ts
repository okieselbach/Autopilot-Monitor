"use client";

import { useEffect } from "react";
import { useSidebar, PageSectionItem } from "../contexts/SidebarContext";

/**
 * Register page-specific sections in the global sidebar.
 * Sections appear below the global nav with a divider.
 * Automatically clears on unmount.
 */
export function usePageSections(
  items: PageSectionItem[],
  title: string,
  mode: "scroll-spy" | "route" = "scroll-spy",
) {
  const { setPageSections, clearPageSections } = useSidebar();

  useEffect(() => {
    if (items.length > 0) {
      setPageSections(items, title, mode);
    }
    return () => clearPageSections();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [items, title, mode]);
}
