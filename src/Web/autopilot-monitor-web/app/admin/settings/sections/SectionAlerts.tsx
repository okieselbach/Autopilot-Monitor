"use client";

import { useAdminConfig } from "../../AdminConfigContext";
import { OpsAlertRulesSection } from "../../components/OpsAlertRulesSection";
import { AdminNotifications } from "../../AdminNotifications";

export function SectionAlerts() {
  const {
    loadingConfig,
    savingOpsAlerts,
    adminConfig,
    opsAlertRules,
    opsAlertTelegramEnabled,
    opsAlertTelegramChatId,
    opsAlertTeamsEnabled,
    opsAlertTeamsWebhookUrl,
    opsAlertSlackEnabled,
    opsAlertSlackWebhookUrl,
    handleSaveOpsAlertConfig,
  } = useAdminConfig();

  return (
    <>
      <AdminNotifications />
      <OpsAlertRulesSection
        loadingConfig={loadingConfig}
        savingOpsAlerts={savingOpsAlerts}
        adminConfigExists={!!adminConfig}
        opsAlertRules={opsAlertRules}
        opsAlertTelegramEnabled={opsAlertTelegramEnabled}
        opsAlertTelegramChatId={opsAlertTelegramChatId}
        opsAlertTeamsEnabled={opsAlertTeamsEnabled}
        opsAlertTeamsWebhookUrl={opsAlertTeamsWebhookUrl}
        opsAlertSlackEnabled={opsAlertSlackEnabled}
        opsAlertSlackWebhookUrl={opsAlertSlackWebhookUrl}
        onSave={handleSaveOpsAlertConfig}
      />
    </>
  );
}
