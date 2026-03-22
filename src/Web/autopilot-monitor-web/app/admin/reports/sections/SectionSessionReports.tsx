"use client";

import { useAdminConfig } from "../../AdminConfigContext";
import { SessionReportsSection } from "../../components/SessionReportsSection";
import { AdminNotifications } from "../../AdminNotifications";

export function SectionSessionReports() {
  const { getAccessToken, setError } = useAdminConfig();
  return (
    <>
      <AdminNotifications />
      <SessionReportsSection getAccessToken={getAccessToken} setError={setError} />
    </>
  );
}
