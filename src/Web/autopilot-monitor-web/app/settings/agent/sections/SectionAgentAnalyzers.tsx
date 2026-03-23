"use client";

import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import AgentAnalyzersSection from "../../components/AgentAnalyzersSection";

export function SectionAgentAnalyzers() {
  const {
    enableLocalAdminAnalyzer, setEnableLocalAdminAnalyzer,
    localAdminAllowedAccounts, setLocalAdminAllowedAccounts,
    newAllowedAccount, setNewAllowedAccount,
    enableSoftwareInventoryAnalyzer, setEnableSoftwareInventoryAnalyzer,
    handleSaveAgentAnalyzers, handleResetAgentAnalyzers,
    savingSection,
  } = useTenantConfig();

  return (
    <>
      <TenantNotifications />
      <AgentAnalyzersSection
        enableLocalAdminAnalyzer={enableLocalAdminAnalyzer}
        setEnableLocalAdminAnalyzer={setEnableLocalAdminAnalyzer}
        localAdminAllowedAccounts={localAdminAllowedAccounts}
        setLocalAdminAllowedAccounts={setLocalAdminAllowedAccounts}
        newAllowedAccount={newAllowedAccount}
        setNewAllowedAccount={setNewAllowedAccount}
        enableSoftwareInventoryAnalyzer={enableSoftwareInventoryAnalyzer}
        setEnableSoftwareInventoryAnalyzer={setEnableSoftwareInventoryAnalyzer}
        onSave={handleSaveAgentAnalyzers}
        onReset={handleResetAgentAnalyzers}
        saving={savingSection === "agentAnalyzers"}
      />
    </>
  );
}
