"use client";

import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import BootstrapSessionsSection from "../../components/BootstrapSessionsSection";

export function SectionBootstrapSessions() {
  const {
    config,
    bootstrapSessions, bootstrapLoading,
    fetchBootstrapSessions, revokeBootstrapSession, createBootstrapSession,
  } = useTenantConfig();

  if (!config?.bootstrapTokenEnabled) {
    return (
      <div className="bg-white rounded-lg shadow p-8 text-center">
        <p className="text-gray-500">Bootstrap Sessions are not available for this tenant.</p>
      </div>
    );
  }

  return (
    <>
      <TenantNotifications />
      <BootstrapSessionsSection
        sessions={bootstrapSessions}
        loading={bootstrapLoading}
        onRefresh={fetchBootstrapSessions}
        onRevoke={revokeBootstrapSession}
        onCreate={createBootstrapSession}
      />
    </>
  );
}
