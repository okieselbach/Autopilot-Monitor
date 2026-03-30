"use client";

import { useMemo } from "react";
import { usePageSections } from "../../hooks/usePageSections";
import { PageSectionItem } from "../../contexts/SidebarContext";
import { useTenantConfig } from "./TenantConfigContext";
import { useAuth } from "../../contexts/AuthContext";
import {
  BuildingOfficeIcon, GearIcon, WrenchScrewdriverIcon, ChartBarIcon,
} from "../../lib/sidebarIcons";

/**
 * Registers all tenant settings sections in the global sidebar
 * with group information for expandable rendering (GitHub-style).
 */
export function SettingsPageSections() {
  const { config, user } = useTenantConfig();
  const { user: authUser } = useAuth();

  const items: PageSectionItem[] = useMemo(() => {
    const sections: PageSectionItem[] = [];
    const isAdmin = user?.isTenantAdmin || user?.isGlobalAdmin;

    // 1. Tenant
    if (isAdmin) {
      sections.push(
        { id: "autopilot", label: "Autopilot Validation", href: "/settings/tenant/autopilot", group: "Tenant", groupIcon: <BuildingOfficeIcon /> },
        { id: "hardware-whitelist", label: "Hardware Whitelist", href: "/settings/tenant/hardware-whitelist", group: "Tenant" },
        { id: "notifications", label: "Notifications", href: "/settings/tenant/notifications", group: "Tenant" },
        { id: "access-management", label: "Access Management", href: "/settings/tenant/access-management", group: "Tenant" },
      );
    }
    if (config?.bootstrapTokenEnabled && (isAdmin || user?.canManageBootstrapTokens)) {
      sections.push(
        { id: "bootstrap-sessions", label: "Bootstrap Sessions", href: "/settings/tenant/bootstrap-sessions", group: "Tenant", ...(!isAdmin ? { groupIcon: <BuildingOfficeIcon /> } : {}) },
      );
    }

    // 2. Agent
    if (isAdmin) {
      sections.push(
        { id: "settings", label: "Agent Settings", href: "/settings/agent/settings", group: "Agent", groupIcon: <GearIcon /> },
        { id: "analyzers", label: "Agent Analyzers", href: "/settings/agent/analyzers", group: "Agent" },
        { id: "diagnostics", label: "Diagnostics Package", href: "/settings/agent/diagnostics", group: "Agent" },
      );
      if (config?.unrestrictedModeEnabled) {
        sections.push(
          { id: "unrestricted-mode", label: "Unrestricted Mode", href: "/settings/agent/unrestricted-mode", group: "Agent" },
        );
      }
    }

    // 3. Maintenance
    if (isAdmin) {
      sections.push(
        { id: "data", label: "Data Management", href: "/settings/management/data", group: "Maintenance", groupIcon: <WrenchScrewdriverIcon /> },
        { id: "offboarding", label: "Offboarding", href: "/settings/management/offboarding", group: "Maintenance" },
      );
    }

    // 4. Reporting (only visible if user has MCP access)
    if (authUser?.hasMcpAccess) {
      sections.push(
        { id: "mcp-usage", label: "MCP Usage", href: "/settings/reporting/mcp-usage", group: "Reporting", groupIcon: <ChartBarIcon /> },
      );
    }

    return sections;
  }, [user, config?.unrestrictedModeEnabled, config?.bootstrapTokenEnabled, authUser?.hasMcpAccess]);

  usePageSections(items, "Configuration", "route");

  return null;
}
