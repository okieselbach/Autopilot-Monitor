"use client";

import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import DiagnosticsSection from "../../components/DiagnosticsSection";

export function SectionDiagnostics() {
  const {
    diagnosticsBlobSasUrl, setDiagnosticsBlobSasUrl,
    diagnosticsUploadMode, setDiagnosticsUploadMode,
    tenantDiagPaths, setTenantDiagPaths,
    globalDiagPaths,
    newDiagPath, setNewDiagPath,
    newDiagDesc, setNewDiagDesc,
    unrestrictedMode,
    handleSaveDiagnostics, handleResetDiagnostics,
    savingSection,
  } = useTenantConfig();

  return (
    <>
      <TenantNotifications />
      <DiagnosticsSection
        diagnosticsBlobSasUrl={diagnosticsBlobSasUrl}
        setDiagnosticsBlobSasUrl={setDiagnosticsBlobSasUrl}
        diagnosticsUploadMode={diagnosticsUploadMode}
        setDiagnosticsUploadMode={setDiagnosticsUploadMode}
        tenantDiagPaths={tenantDiagPaths}
        setTenantDiagPaths={setTenantDiagPaths}
        globalDiagPaths={globalDiagPaths}
        newDiagPath={newDiagPath}
        setNewDiagPath={setNewDiagPath}
        newDiagDesc={newDiagDesc}
        setNewDiagDesc={setNewDiagDesc}
        unrestrictedMode={unrestrictedMode}
        onSave={handleSaveDiagnostics}
        onReset={handleResetDiagnostics}
        saving={savingSection === "diagnostics"}
      />
    </>
  );
}
