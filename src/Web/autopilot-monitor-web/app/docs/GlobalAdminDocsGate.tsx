"use client";

import dynamic from "next/dynamic";
import type { ComponentType } from "react";
import { useAuth } from "../../contexts/AuthContext";

const GLOBAL_ADMIN_SECTIONS: Record<string, ComponentType> = {
  "agent-internals": dynamic(() => import("./sections/SectionAgentInternals").then((m) => ({ default: m.SectionAgentInternals }))),
};

/**
 * Client-side gate for Global-Admin-only docs sections.
 * Only loads the section content via dynamic import AFTER verifying auth + isGlobalAdmin.
 * Returns null (renders nothing) for unauthenticated or non-admin users.
 */
export function GlobalAdminDocsGate({ sectionId }: { sectionId: string }) {
  const { user, isLoading, isAuthenticated } = useAuth();

  if (isLoading) {
    return (
      <div className="flex justify-center py-16">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-600" />
      </div>
    );
  }

  if (!isAuthenticated || !user?.isGlobalAdmin) {
    return null;
  }

  const Section = GLOBAL_ADMIN_SECTIONS[sectionId];
  if (!Section) return null;

  return <Section />;
}
