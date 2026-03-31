"use client";

import { useAdminConfig } from "../../AdminConfigContext";
import { DistressReportsSection } from "../../components/DistressReportsSection";
import { AdminNotifications } from "../../AdminNotifications";

export function SectionDistressReports() {
  const { getAccessToken, setError } = useAdminConfig();
  return (
    <>
      <AdminNotifications />
      <DistressReportsSection getAccessToken={getAccessToken} setError={setError} />
    </>
  );
}
