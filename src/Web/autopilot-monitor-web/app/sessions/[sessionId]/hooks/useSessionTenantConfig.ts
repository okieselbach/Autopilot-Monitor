"use client";

import { useEffect, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch } from "@/lib/authenticatedFetch";

interface UseSessionTenantConfigReturn {
  showScriptOutput: boolean;
  enableSoftwareInventoryAnalyzer: boolean;
}

/**
 * Fetches the session's tenant-level UI config (best-effort).
 * Triggers once sessionTenantId is known. Swallows errors — these flags
 * are non-critical (fall back to sensible defaults).
 */
export function useSessionTenantConfig(
  sessionTenantId: string | null,
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>,
): UseSessionTenantConfigReturn {
  const [showScriptOutput, setShowScriptOutput] = useState(true);
  const [enableSoftwareInventoryAnalyzer, setEnableSoftwareInventoryAnalyzer] = useState(false);

  useEffect(() => {
    if (!sessionTenantId) return;
    let cancelled = false;
    (async () => {
      try {
        const res = await authenticatedFetch(api.config.tenant(sessionTenantId), getAccessToken);
        if (!res.ok || cancelled) return;
        const cfg = await res.json();
        if (cancelled) return;
        setShowScriptOutput(cfg.showScriptOutput ?? true);
        setEnableSoftwareInventoryAnalyzer(cfg.enableSoftwareInventoryAnalyzer ?? false);
      } catch { /* non-fatal */ }
    })();
    return () => { cancelled = true; };
  }, [sessionTenantId, getAccessToken]);

  return { showScriptOutput, enableSoftwareInventoryAnalyzer };
}
