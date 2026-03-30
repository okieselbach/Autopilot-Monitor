"use client";

import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import HardwareWhitelistSection from "../../components/HardwareWhitelistSection";

export function SectionHardwareWhitelist() {
  const {
    manufacturerWhitelist, setManufacturerWhitelist,
    modelWhitelist, setModelWhitelist,
    handleSaveHardwareWhitelist, handleResetHardwareWhitelist,
    savingSection,
  } = useTenantConfig();

  return (
    <>
      <TenantNotifications />
      <HardwareWhitelistSection
        manufacturerWhitelist={manufacturerWhitelist}
        setManufacturerWhitelist={setManufacturerWhitelist}
        modelWhitelist={modelWhitelist}
        setModelWhitelist={setModelWhitelist}
        onSave={handleSaveHardwareWhitelist}
        onReset={handleResetHardwareWhitelist}
        saving={savingSection === "hardwareWhitelist"}
      />
    </>
  );
}
