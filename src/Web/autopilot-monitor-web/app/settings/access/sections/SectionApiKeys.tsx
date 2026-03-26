"use client";

import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import ApiKeysSection from "../../components/ApiKeysSection";

export function SectionApiKeys() {
  const { getAccessToken, setError, setSuccessMessage } = useTenantConfig();
  return (
    <>
      <TenantNotifications />
      <ApiKeysSection
        getAccessToken={getAccessToken}
        setError={setError}
        setSuccessMessage={setSuccessMessage}
        isGlobalAdmin={false}
      />
    </>
  );
}
