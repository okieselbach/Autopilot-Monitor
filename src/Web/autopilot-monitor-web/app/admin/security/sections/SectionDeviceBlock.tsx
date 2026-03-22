"use client";

import { useAdminConfig } from "../../AdminConfigContext";
import { DeviceBlockSection } from "../../components/DeviceBlockSection";
import { AdminNotifications } from "../../AdminNotifications";

export function SectionDeviceBlock() {
  const {
    tenants,
    maxSessionWindowHours, setMaxSessionWindowHours,
    maintenanceBlockDurationHours, setMaintenanceBlockDurationHours,
    savingConfig, adminConfig,
    handleSaveAdminConfig, getAccessToken, setError, setSuccessMessage,
  } = useAdminConfig();

  return (
    <>
      <AdminNotifications />
      <DeviceBlockSection
        tenants={tenants}
        maxSessionWindowHours={maxSessionWindowHours}
        setMaxSessionWindowHours={setMaxSessionWindowHours}
        maintenanceBlockDurationHours={maintenanceBlockDurationHours}
        setMaintenanceBlockDurationHours={setMaintenanceBlockDurationHours}
        savingConfig={savingConfig}
        adminConfigExists={!!adminConfig}
        onSaveAdminConfig={handleSaveAdminConfig}
        getAccessToken={getAccessToken}
        setError={setError}
        setSuccessMessage={setSuccessMessage}
      />
    </>
  );
}
