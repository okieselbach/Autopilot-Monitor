"use client";

import { useAdminConfig } from "../../AdminConfigContext";
import { DiagnosticsLogPathsSection } from "../../components/DiagnosticsLogPathsSection";
import { AdminNotifications } from "../../AdminNotifications";

export function SectionDiagnosticsLogPaths() {
  const { globalDiagPaths, setGlobalDiagPaths, loadingConfig, savingDiagPaths, adminConfig, handleSaveDiagPaths } = useAdminConfig();

  return (
    <>
      <AdminNotifications />
      <DiagnosticsLogPathsSection
        globalDiagPaths={globalDiagPaths}
        setGlobalDiagPaths={setGlobalDiagPaths}
        loadingConfig={loadingConfig}
        savingDiagPaths={savingDiagPaths}
        adminConfigExists={!!adminConfig}
        onSave={handleSaveDiagPaths}
      />
    </>
  );
}
