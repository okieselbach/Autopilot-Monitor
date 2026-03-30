"use client";

import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import NotificationsSection from "../../components/NotificationsSection";

export function SectionNotifications() {
  const {
    webhookProviderType, setWebhookProviderType,
    webhookUrl, setWebhookUrl,
    webhookNotifyOnSuccess, setWebhookNotifyOnSuccess,
    webhookNotifyOnFailure, setWebhookNotifyOnFailure,
    handleTestWebhook, testingWebhook, testWebhookResult,
    handleSaveNotifications, handleResetNotifications,
    savingSection,
  } = useTenantConfig();

  return (
    <>
      <TenantNotifications />
      <NotificationsSection
        webhookProviderType={webhookProviderType}
        setWebhookProviderType={setWebhookProviderType}
        webhookUrl={webhookUrl}
        setWebhookUrl={setWebhookUrl}
        webhookNotifyOnSuccess={webhookNotifyOnSuccess}
        setWebhookNotifyOnSuccess={setWebhookNotifyOnSuccess}
        webhookNotifyOnFailure={webhookNotifyOnFailure}
        setWebhookNotifyOnFailure={setWebhookNotifyOnFailure}
        onTestWebhook={handleTestWebhook}
        testingWebhook={testingWebhook}
        testWebhookResult={testWebhookResult}
        onSave={handleSaveNotifications}
        onReset={handleResetNotifications}
        saving={savingSection === "notifications"}
      />
    </>
  );
}
