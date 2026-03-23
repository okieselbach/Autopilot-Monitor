"use client";

import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import DataManagementSection from "../../components/DataManagementSection";

export function SectionDataManagement() {
  const {
    dataRetentionDays, setDataRetentionDays,
    sessionTimeoutHours, setSessionTimeoutHours,
    user,
    handleSaveDataManagement, handleResetDataManagement,
    savingSection,
  } = useTenantConfig();

  return (
    <>
      <TenantNotifications />
      <DataManagementSection
        dataRetentionDays={dataRetentionDays}
        setDataRetentionDays={setDataRetentionDays}
        sessionTimeoutHours={sessionTimeoutHours}
        setSessionTimeoutHours={setSessionTimeoutHours}
        isGalacticAdmin={user?.isGalacticAdmin}
        onSave={handleSaveDataManagement}
        onReset={handleResetDataManagement}
        saving={savingSection === "dataManagement"}
      />
    </>
  );
}
