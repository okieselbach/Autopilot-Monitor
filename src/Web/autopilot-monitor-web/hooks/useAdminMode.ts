"use client";

import { useState, useEffect } from "react";

interface UseAdminModeReturn {
  adminMode: boolean;
  setAdminMode: (value: boolean) => void;
  globalAdminMode: boolean;
  setGlobalAdminMode: (value: boolean) => void;
}

/**
 * Manages adminMode and globalAdminMode state with localStorage persistence.
 * Changes are persisted to localStorage and broadcast via a 'localStorageChange'
 * custom event so all components on the same page stay in sync.
 * Cross-tab synchronization uses the native 'storage' event.
 */
export function useAdminMode(): UseAdminModeReturn {
  const [adminMode, setAdminModeState] = useState<boolean>(() => {
    if (typeof window !== "undefined") {
      return localStorage.getItem("adminMode") === "true";
    }
    return false;
  });

  const [globalAdminMode, setGlobalAdminModeState] = useState<boolean>(() => {
    if (typeof window !== "undefined") {
      return localStorage.getItem("globalAdminMode") === "true";
    }
    return false;
  });

  // Persist to localStorage and notify same-tab listeners on change
  useEffect(() => {
    localStorage.setItem("adminMode", adminMode.toString());
    window.dispatchEvent(new Event("localStorageChange"));
  }, [adminMode]);

  useEffect(() => {
    localStorage.setItem("globalAdminMode", globalAdminMode.toString());
    window.dispatchEvent(new Event("localStorageChange"));
  }, [globalAdminMode]);

  // Sync from external changes (cross-tab via 'storage', same-tab via 'localStorageChange')
  useEffect(() => {
    const handleStorageChange = (e: StorageEvent) => {
      if (e.key === "adminMode" && e.newValue !== null) {
        setAdminModeState(e.newValue === "true");
      }
      if (e.key === "globalAdminMode" && e.newValue !== null) {
        setGlobalAdminModeState(e.newValue === "true");
      }
    };

    const handleCustomStorageChange = () => {
      const newAdminMode = localStorage.getItem("adminMode") === "true";
      const newGlobalMode = localStorage.getItem("globalAdminMode") === "true";
      setAdminModeState((prev) => (prev !== newAdminMode ? newAdminMode : prev));
      setGlobalAdminModeState((prev) =>
        prev !== newGlobalMode ? newGlobalMode : prev
      );
    };

    window.addEventListener("storage", handleStorageChange);
    window.addEventListener("localStorageChange", handleCustomStorageChange);
    return () => {
      window.removeEventListener("storage", handleStorageChange);
      window.removeEventListener(
        "localStorageChange",
        handleCustomStorageChange
      );
    };
  }, []);

  return {
    adminMode,
    setAdminMode: setAdminModeState,
    globalAdminMode,
    setGlobalAdminMode: setGlobalAdminModeState,
  };
}
