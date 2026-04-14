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
    modernDeploymentHarmlessEventIds, setModernDeploymentHarmlessEventIds,
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
        modernDeploymentHarmlessEventIds={modernDeploymentHarmlessEventIds}
        setModernDeploymentHarmlessEventIds={setModernDeploymentHarmlessEventIds}
        onSave={handleSaveAdminConfig}
        onReset={handleResetAdminConfig}
      />
    </>
  );
}
