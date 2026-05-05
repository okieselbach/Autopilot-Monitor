import { describe, it, expect } from "vitest";
import { canFetchTenantNotifications } from "../../contexts/tenantNotificationsGate";

/**
 * CORRECTNESS GUARD: The bell is visible to every tenant member (Admin/Operator/Viewer)
 * and to Global Admins. Polling for users who do NOT match either category produces a
 * permanent 401/403 storm — the original bug that motivated PR-N5. This test locks in
 * the gating predicate so that future refactors of the role check do not regress to the
 * old "fetch on user != null" condition.
 */
describe("canFetchTenantNotifications", () => {
  it("returns false for unauthenticated user", () => {
    expect(canFetchTenantNotifications(null)).toBe(false);
    expect(canFetchTenantNotifications(undefined)).toBe(false);
  });

  it("returns false for authenticated user without tenant role and not GA", () => {
    expect(canFetchTenantNotifications({ role: null, isGlobalAdmin: false })).toBe(false);
  });

  it.each([
    ["Admin", "Admin"],
    ["Operator", "Operator"],
    ["Viewer", "Viewer"],
  ])("returns true for tenant role %s", (_label, role) => {
    expect(canFetchTenantNotifications({ role, isGlobalAdmin: false })).toBe(true);
  });

  it("returns true for Global Admin without tenant role", () => {
    expect(canFetchTenantNotifications({ role: null, isGlobalAdmin: true })).toBe(true);
  });

  it("returns true for Global Admin with tenant role", () => {
    expect(canFetchTenantNotifications({ role: "Admin", isGlobalAdmin: true })).toBe(true);
  });
});
