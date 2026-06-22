"use client";

import { useEffect, useState } from "react";
import { useTenant } from "@/contexts/TenantContext";
import { useAuth } from "@/contexts/AuthContext";
import { useAdminMode } from "@/hooks/useAdminMode";
import { useTenantList, type TenantInfo } from "@/hooks/useTenantList";

export type { TenantInfo };

export interface GlobalAdminScope {
  /**
   * True when global-scope mode is toggled on AND the user has platform scope (Global Admin OR the
   * read-only Global Reader). This is a VISIBILITY/routing flag — it drives the tenant selector, the
   * "Global view" banner and the `/global/` endpoint choice, all of which are read-only-safe. Mutating
   * actions on these pages must gate separately on the real Global-Admin / own-tenant-admin status.
   */
  isGlobalAdmin: boolean;
  /** Sorted tenant list for the selector. Empty unless {@link isGlobalAdmin}. */
  tenants: TenantInfo[];
  /** Currently selected tenant in the scope selector. */
  selectedTenantId: string;
  setSelectedTenantId: (id: string) => void;
  /** Tenant to actually query: the GA-override target if one is picked, else the user's own tenant. */
  effectiveTenantId: string;
  /** GA picked a tenant other than their own → call the cross-tenant `/global/` endpoints. */
  isGlobalOverride: boolean;
  /** GA mode with no tenant selected → aggregated cross-tenant view. */
  isAggregatedGlobalView: boolean;
}

/**
 * Global-Admin tenant scope for the **override-only** page variant (gather-rules, analyze-rules,
 * sla, usage-metrics): the selection always resolves to a concrete tenant — defaulting to the
 * caller's own tenant — and endpoint choice is keyed on {@link GlobalAdminScope.isGlobalOverride}.
 * There is no aggregated "All tenants" mode here; for that use
 * {@link "@/hooks/useAggregatedAdminScope".useAggregatedAdminScope}.
 *
 * Pair with {@link "@/components/TenantScopeSelector".TenantScopeSelector} for the header dropdown
 * and {@link "@/components/GlobalAdminBanner".GlobalAdminBanner} for the "Global Admin View" bar.
 */
export function useGlobalAdminScope(): GlobalAdminScope {
  const { tenantId } = useTenant();
  const { hasGlobalScope } = useAuth();
  const { globalAdminMode } = useAdminMode();

  // Platform scope (GA or read-only Global Reader) — read-only-safe cross-tenant view + selector.
  const isGlobalAdmin = Boolean(globalAdminMode && hasGlobalScope);

  const tenants = useTenantList(isGlobalAdmin);
  const [selectedTenantId, setSelectedTenantId] = useState<string>("");

  // Default the selection to the user's own tenant (never empty/aggregated in this variant).
  useEffect(() => {
    if (tenantId && !selectedTenantId) {
      setSelectedTenantId(tenantId);
    }
  }, [tenantId, selectedTenantId]);

  const isGlobalOverride = Boolean(
    isGlobalAdmin && selectedTenantId && selectedTenantId !== tenantId
  );
  const effectiveTenantId =
    isGlobalAdmin && selectedTenantId ? selectedTenantId : tenantId;
  const isAggregatedGlobalView = Boolean(isGlobalAdmin && !selectedTenantId);

  return {
    isGlobalAdmin,
    tenants,
    selectedTenantId,
    setSelectedTenantId,
    effectiveTenantId,
    isGlobalOverride,
    isAggregatedGlobalView,
  };
}
