"use client";

import { useAuth } from "@/contexts/AuthContext";
import { useTenant } from "@/contexts/TenantContext";
import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import OffboardingSection from "../../components/OffboardingSection";

export function SectionOffboarding() {
  const {
    showOffboardDialog, setShowOffboardDialog,
    offboardConfirmText, setOffboardConfirmText,
    offboarding, offboardError, setOffboardError,
    handleOffboard,
    offboardingInProgress, handleDrainBarrierElapsed,
  } = useTenantConfig();
  const { tenantId } = useTenant();
  const { getAccessToken } = useAuth();

  return (
    <>
      <TenantNotifications />
      <OffboardingSection
        showOffboardDialog={showOffboardDialog}
        setShowOffboardDialog={setShowOffboardDialog}
        offboardConfirmText={offboardConfirmText}
        setOffboardConfirmText={setOffboardConfirmText}
        offboarding={offboarding}
        offboardError={offboardError}
        setOffboardError={setOffboardError}
        onOffboard={handleOffboard}
        offboardingInProgress={offboardingInProgress}
        onDrainBarrierElapsed={handleDrainBarrierElapsed}
        tenantId={tenantId}
        getAccessToken={getAccessToken}
      />
    </>
  );
}
