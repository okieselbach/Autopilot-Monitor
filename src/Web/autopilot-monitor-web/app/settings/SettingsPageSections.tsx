"use client";

import { useMemo } from "react";
import { usePageSections } from "../../hooks/usePageSections";
import { PageSectionItem } from "../../contexts/SidebarContext";
import { useTenantConfig } from "./TenantConfigContext";
import {
  ShieldCheckIcon, GearIcon, UsersIcon, BellIcon, CircleStackIcon, ChartBarIcon,
} from "../../lib/sidebarIcons";

/**
 * Registers all tenant settings sections in the global sidebar
 * with group information for expandable rendering (GitHub-style).
 */
export function SettingsPageSections() {
  const { config, user } = useTenantConfig();

  const items: PageSectionItem[] = useMemo(() => {
    const sections: PageSectionItem[] = [];
    const isAdmin = user?.isTenantAdmin || user?.isGlobalAdmin;

    // 1. Validation
    if (isAdmin) {
      sections.push(
        { id: "autopilot", label: "Autopilot Validation", href: "/settings/validation/autopilot", group: "Validation", groupIcon: <ShieldCheckIcon /> },
        { id: "hardware-whitelist", label: "Hardware Whitelist", href: "/settings/validation/hardware-whitelist", group: "Validation" },
      );
    }

    // 2. Agent
    if (isAdmin) {
      sections.push(
        { id: "settings", label: "Agent Settings", href: "/settings/agent/settings", group: "Agent", groupIcon: <GearIcon /> },
        { id: "analyzers", label: "Agent Analyzers", href: "/settings/agent/analyzers", group: "Agent" },
      );
      if (config?.unrestrictedModeEnabled) {
        sections.push(
          { id: "unrestricted-mode", label: "Unrestricted Mode", href: "/settings/agent/unrestricted-mode", group: "Agent" },
        );
      }
    }

    // 3. Access
    if (isAdmin) {
      sections.push(
        { id: "admin-management", label: "Admin Management", href: "/settings/access/admin-management", group: "Access", groupIcon: <UsersIcon /> },
      );
    }
    if (config?.bootstrapTokenEnabled && (isAdmin || user?.canManageBootstrapTokens)) {
      sections.push(
        { id: "bootstrap-sessions", label: "Bootstrap Sessions", href: "/settings/access/bootstrap-sessions", group: "Access", ...(!isAdmin ? { groupIcon: <UsersIcon /> } : {}) },
      );
    }

    // 4. Integrations
    if (isAdmin) {
      sections.push(
        { id: "notifications", label: "Notifications", href: "/settings/integrations/notifications", group: "Integrations", groupIcon: <BellIcon /> },
        { id: "diagnostics", label: "Diagnostics", href: "/settings/integrations/diagnostics", group: "Integrations" },
      );
    }

    // 5. Management
    if (isAdmin) {
      sections.push(
        { id: "data", label: "Data Management", href: "/settings/management/data", group: "Management", groupIcon: <CircleStackIcon /> },
        { id: "offboarding", label: "Offboarding", href: "/settings/management/offboarding", group: "Management" },
      );
    }

    // 6. MCP (visible to any authenticated user — the API itself checks MCP access)
    sections.push(
      { id: "mcp-usage", label: "Usage", href: "/settings/mcp/usage", group: "MCP", groupIcon: <ChartBarIcon /> },
    );

    return sections;
  }, [user, config?.unrestrictedModeEnabled, config?.bootstrapTokenEnabled]);

  usePageSections(items, "Configuration", "route");

  return null;
}
