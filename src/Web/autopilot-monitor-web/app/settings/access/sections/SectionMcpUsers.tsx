"use client";

import { TenantNotifications } from "../../TenantNotifications";
import McpUsersSection from "../../components/McpUsersSection";

export function SectionMcpUsers() {
  return (
    <>
      <TenantNotifications />
      <McpUsersSection />
    </>
  );
}
