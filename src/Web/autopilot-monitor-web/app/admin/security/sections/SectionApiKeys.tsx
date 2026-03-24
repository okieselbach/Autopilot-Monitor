"use client";

import { useAdminConfig } from "../../AdminConfigContext";
import { AdminNotifications } from "../../AdminNotifications";
import ApiKeysSection from "../../components/ApiKeysSection";

export function SectionApiKeys() {
  const { getAccessToken, setError, setSuccessMessage, tenants } = useAdminConfig();
  return (
    <>
      <AdminNotifications />
      <ApiKeysSection
        getAccessToken={getAccessToken}
        setError={setError}
        setSuccessMessage={setSuccessMessage}
        isGlobalAdmin={true}
        tenants={tenants.map((t) => ({ tenantId: t.tenantId, displayName: t.domainName ?? t.tenantId }))}
      />
    </>
  );
}
