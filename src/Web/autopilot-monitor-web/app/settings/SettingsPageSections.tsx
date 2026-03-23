"use client";

import { useMemo } from "react";
import { usePageSections } from "../../hooks/usePageSections";
import { PageSectionItem } from "../../contexts/SidebarContext";
import { useTenantConfig } from "./TenantConfigContext";
import {
  ShieldCheckIcon, CpuChipIcon, GearIcon, MagnifyingGlassIcon,
  LockClosedIcon, UsersIcon, KeyIcon, BellIcon, CloudArrowUpIcon,
  CircleStackIcon, ArrowRightOnRectangleIcon,
} from "../../lib/sidebarIcons";

/**
 * Registers all tenant settings sections in the global sidebar
 * with group information for expandable rendering.
 * Placed inside TenantConfigProvider so it can read config for conditional sections.
 */
export function SettingsPageSections() {
  const { config, user } = useTenantConfig();

  const items: PageSectionItem[] = useMemo(() => {
    const sections: PageSectionItem[] = [];

    // 1. Validation
    if (user?.isTenantAdmin || user?.isGalacticAdmin) {
      sections.push(
        { id: "autopilot", label: "Autopilot Validation", icon: <ShieldCheckIcon />, href: "/settings/validation/autopilot", group: "Validation" },
        { id: "hardware-whitelist", label: "Hardware Whitelist", icon: <CpuChipIcon />, href: "/settings/validation/hardware-whitelist", group: "Validation" },
      );
    }

    // 2. Agent
    if (user?.isTenantAdmin || user?.isGalacticAdmin) {
      sections.push(
        { id: "settings", label: "Agent Settings", icon: <GearIcon />, href: "/settings/agent/settings", group: "Agent" },
        { id: "analyzers", label: "Agent Analyzers", icon: <MagnifyingGlassIcon />, href: "/settings/agent/analyzers", group: "Agent" },
      );
      if (config?.unrestrictedModeEnabled) {
        sections.push(
          { id: "unrestricted-mode", label: "Unrestricted Mode", icon: <LockClosedIcon />, href: "/settings/agent/unrestricted-mode", group: "Agent" },
        );
      }
    }

    // 3. Access
    if (user?.isTenantAdmin || user?.isGalacticAdmin) {
      sections.push(
        { id: "admin-management", label: "Admin Management", icon: <UsersIcon />, href: "/settings/access/admin-management", group: "Access" },
      );
    }
    if (config?.bootstrapTokenEnabled && (user?.isTenantAdmin || user?.isGalacticAdmin || user?.canManageBootstrapTokens)) {
      sections.push(
        { id: "bootstrap-sessions", label: "Bootstrap Sessions", icon: <KeyIcon />, href: "/settings/access/bootstrap-sessions", group: "Access" },
      );
    }

    // 4. Integrations
    if (user?.isTenantAdmin || user?.isGalacticAdmin) {
      sections.push(
        { id: "notifications", label: "Notifications", icon: <BellIcon />, href: "/settings/integrations/notifications", group: "Integrations" },
        { id: "diagnostics", label: "Diagnostics", icon: <CloudArrowUpIcon />, href: "/settings/integrations/diagnostics", group: "Integrations" },
      );
    }

    // 5. Management
    if (user?.isTenantAdmin || user?.isGalacticAdmin) {
      sections.push(
        { id: "data", label: "Data Management", icon: <CircleStackIcon />, href: "/settings/management/data", group: "Management" },
        { id: "offboarding", label: "Offboarding", icon: <ArrowRightOnRectangleIcon />, href: "/settings/management/offboarding", group: "Management" },
      );
    }

    return sections;
  }, [user, config?.unrestrictedModeEnabled, config?.bootstrapTokenEnabled]);

  usePageSections(items, "Configuration", "route");

  return null;
}
