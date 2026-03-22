"use client";

import { useAdminConfig } from "../../AdminConfigContext";
import { TenantManagementSection } from "../../components/TenantManagementSection";

export function SectionTenantManagement() {
  const {
    tenants,
    loadingTenants,
    fetchTenants,
    previewApproved,
    setPreviewApproved,
    setTenants,
    getAccessToken,
    setError,
    setSuccessMessage,
  } = useAdminConfig();

  return (
    <TenantManagementSection
      tenants={tenants}
      loadingTenants={loadingTenants}
      fetchTenants={fetchTenants}
      previewApproved={previewApproved}
      setPreviewApproved={setPreviewApproved}
      setTenants={setTenants}
      getAccessToken={getAccessToken}
      setError={setError}
      setSuccessMessage={setSuccessMessage}
    />
  );
}
