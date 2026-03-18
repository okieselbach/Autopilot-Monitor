"use client";

import { useState, useCallback } from "react";

export type CollapseState = "full" | "icons" | "hidden";

const STORAGE_KEY = "sidebar-collapse-state";
const CYCLE_ORDER: CollapseState[] = ["full", "icons", "hidden"];

function readState(): CollapseState {
  if (typeof window === "undefined") return "full";
  const stored = localStorage.getItem(STORAGE_KEY);
  if (stored === "full" || stored === "icons" || stored === "hidden") return stored;
  return "full";
}

export function useSidebarState() {
  const [collapseState, setCollapseStateRaw] = useState<CollapseState>(readState);

  const setCollapseState = useCallback((state: CollapseState) => {
    setCollapseStateRaw(state);
    if (typeof window !== "undefined") {
      localStorage.setItem(STORAGE_KEY, state);
    }
  }, []);

  const cycleCollapseState = useCallback(() => {
    setCollapseState(
      CYCLE_ORDER[(CYCLE_ORDER.indexOf(readState()) + 1) % CYCLE_ORDER.length]
    );
  }, [setCollapseState]);

  return { collapseState, setCollapseState, cycleCollapseState };
}
