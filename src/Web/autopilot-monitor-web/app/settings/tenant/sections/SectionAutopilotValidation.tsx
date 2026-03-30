"use client";

import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import AutopilotValidationSection from "../../components/AutopilotValidationSection";

export function SectionAutopilotValidation() {
  const {
    validateAutopilotDevice, setValidateAutopilotDevice,
    validateCorporateIdentifier, setValidateCorporateIdentifier,
    autopilotConsentInProgress, savingSection,
    beginDeviceValidationConsentFlow,
  } = useTenantConfig();

  return (
    <>
      <TenantNotifications />
      <AutopilotValidationSection
        validateAutopilotDevice={validateAutopilotDevice}
        setValidateAutopilotDevice={setValidateAutopilotDevice}
        validateCorporateIdentifier={validateCorporateIdentifier}
        setValidateCorporateIdentifier={setValidateCorporateIdentifier}
        autopilotConsentInProgress={autopilotConsentInProgress}
        saving={savingSection === "autopilotValidation"}
        onBeginConsent={beginDeviceValidationConsentFlow}
      />
    </>
  );
}
