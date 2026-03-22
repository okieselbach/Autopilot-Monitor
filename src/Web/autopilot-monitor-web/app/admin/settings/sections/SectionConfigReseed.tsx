"use client";

import { useAdminConfig } from "../../AdminConfigContext";
import { ConfigReseedSection } from "../../components/ConfigReseedSection";
import { AdminNotifications } from "../../AdminNotifications";

export function SectionConfigReseed() {
  const { getAccessToken, setError, setSuccessMessage } = useAdminConfig();

  return (
    <>
      <AdminNotifications />
      <ConfigReseedSection
        getAccessToken={getAccessToken}
        setError={setError}
        setSuccessMessage={setSuccessMessage}
      />
    </>
  );
}
