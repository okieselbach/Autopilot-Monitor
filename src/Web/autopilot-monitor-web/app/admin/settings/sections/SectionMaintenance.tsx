"use client";

import { useAdminConfig } from "../../AdminConfigContext";
import { MaintenanceSection } from "../../components/MaintenanceSection";
import { AdminNotifications } from "../../AdminNotifications";

export function SectionMaintenance() {
  const { getAccessToken, setError, setSuccessMessage } = useAdminConfig();

  return (
    <>
      <AdminNotifications />
      <MaintenanceSection
        getAccessToken={getAccessToken}
        setError={setError}
        setSuccessMessage={setSuccessMessage}
      />
    </>
  );
}
