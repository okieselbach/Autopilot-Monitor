"use client";

import dynamic from "next/dynamic";
import type { ComponentType } from "react";
import { useAuth } from "../../contexts/AuthContext";

const MCP_SECTIONS: Record<string, ComponentType> = {
  "mcp-integration": dynamic(() => import("./sections/SectionMcpIntegration").then((m) => ({ default: m.SectionMcpIntegration }))),
};

/**
 * Client-side gate for MCP-only docs sections.
 * Only loads the section content via dynamic import AFTER verifying auth + MCP access.
 * Returns null (renders nothing) for unauthenticated or non-MCP users —
 * the server-side page.tsx calls notFound() for unknown sections,
 * and this component handles the "known but unauthorized" case.
 */
export function McpDocsGate({ sectionId }: { sectionId: string }) {
  const { user, isLoading, isAuthenticated } = useAuth();

  if (isLoading) {
    return (
      <div className="flex justify-center py-16">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-600" />
      </div>
    );
  }

  if (!isAuthenticated || !user?.hasMcpAccess) {
    // Return empty — indistinguishable from a non-existent page.
    // The sidebar already hides this section for unauthorized users.
    return null;
  }

  const Section = MCP_SECTIONS[sectionId];
  if (!Section) return null;

  return <Section />;
}
