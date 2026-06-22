"use client";

import { useAuth } from "@/contexts/AuthContext";

/**
 * True only for a real platform Global Admin — the single signal that gates every MUTATING control in
 * the cross-tenant /admin area. A read-only Global Reader reaches these pages (view scope via
 * hasGlobalScope) but must NOT be able to block/kill devices, trigger maintenance, edit tenants, etc.
 * The backend also enforces this (GlobalAdminOnly → 403); this hook is the UI read-only contract.
 */
export function useCanMutatePlatform(): boolean {
  const { user } = useAuth();
  return user?.isGlobalAdmin === true;
}
