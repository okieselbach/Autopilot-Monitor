"use client";

import { useAdminConfig } from "../../AdminConfigContext";
import { AdminConfigSettingsSection } from "../../components/AdminConfigSettingsSection";
import { AdminNotifications } from "../../AdminNotifications";

export function SectionGlobalSettings() {
  const {
    loadingConfig, savingConfig, adminConfig,
    globalRateLimit, setGlobalRateLimit,
    platformStatsBlobSasUrl, setPlatformStatsBlobSasUrl,
    collectorIdleTimeoutMinutes, setCollectorIdleTimeoutMinutes,
    allowAgentDowngrade, setAllowAgentDowngrade,
    handleSaveAdminConfig, handleResetAdminConfig,
  } = useAdminConfig();

  return (
    <>
      <AdminNotifications />
      <AdminConfigSettingsSection
        loadingConfig={loadingConfig}
        savingConfig={savingConfig}
        adminConfig={adminConfig}
        globalRateLimit={globalRateLimit}
        setGlobalRateLimit={setGlobalRateLimit}
        platformStatsBlobSasUrl={platformStatsBlobSasUrl}
        setPlatformStatsBlobSasUrl={setPlatformStatsBlobSasUrl}
        collectorIdleTimeoutMinutes={collectorIdleTimeoutMinutes}
        setCollectorIdleTimeoutMinutes={setCollectorIdleTimeoutMinutes}
        allowAgentDowngrade={allowAgentDowngrade}
        setAllowAgentDowngrade={setAllowAgentDowngrade}
        onSave={handleSaveAdminConfig}
        onReset={handleResetAdminConfig}
      />
    </>
  );
}
