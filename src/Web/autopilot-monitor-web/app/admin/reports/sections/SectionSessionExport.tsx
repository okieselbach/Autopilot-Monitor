"use client";

import { useAdminConfig } from "../../AdminConfigContext";
import { SessionExportSection } from "../../components/SessionExportSection";
import { AdminNotifications } from "../../AdminNotifications";

export function SectionSessionExport() {
  const { tenants, getAccessToken } = useAdminConfig();
  return (
    <>
      <AdminNotifications />
      <SessionExportSection tenants={tenants} getAccessToken={getAccessToken} />
    </>
  );
}
