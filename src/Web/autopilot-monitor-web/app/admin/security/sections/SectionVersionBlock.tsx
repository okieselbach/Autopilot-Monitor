"use client";

import { useAdminConfig } from "../../AdminConfigContext";
import { VersionBlockSection } from "../../components/VersionBlockSection";
import { AdminNotifications } from "../../AdminNotifications";

export function SectionVersionBlock() {
  const { getAccessToken, setError, setSuccessMessage } = useAdminConfig();
  return (
    <>
      <AdminNotifications />
      <VersionBlockSection
        getAccessToken={getAccessToken}
        setError={setError}
        setSuccessMessage={setSuccessMessage}
      />
    </>
  );
}
